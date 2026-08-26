// Orchestrates one turn while keeping ownership, auditing, budgets, and tools outside model control.
using System.Runtime.CompilerServices;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using Fadada.CertificationQueryAgent.AgentHost.Middleware;
using Fadada.CertificationQueryAgent.AgentHost.Prompts;
using Fadada.CertificationQueryAgent.AgentHost.Tools;
using Fadada.CertificationQueryAgent.AgentHost.Telemetry;
using Fadada.CertificationQueryAgent.Application.AgentTurns;
using Fadada.CertificationQueryAgent.Application.Auditing;
using Fadada.CertificationQueryAgent.Application.Common;
using Fadada.CertificationQueryAgent.Application.Conversations;
using Fadada.CertificationQueryAgent.Application.DomainTools;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Fadada.CertificationQueryAgent.AgentHost.Runtime;

/// <summary>
/// 编排模型、受控工具、会话状态和审计，构成单 Agent 查询回合的运行核心。
/// </summary>
public sealed class DomainAgentRuntime : IAgentRuntime
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IConversationStore _conversationStore;
    private readonly IToolPolicyPipeline _policyPipeline;
    private readonly AgentRuntimeOptions _options;
    private readonly DomainQueryPrompt _prompt;
    private readonly ChatClientAgent _chatClientAgent;
    private readonly AIAgent _agentPipeline;
    private readonly IReadOnlyList<AIFunction> _tools;

    public DomainAgentRuntime(
        IChatClient modelClient,
        IConversationStore conversationStore,
        IToolPolicyPipeline policyPipeline,
        AgentRuntimeOptions? options = null,
        IModelCallAuditStore? modelCallAuditStore = null,
        ModelPricing? modelPricing = null)
    {
        ArgumentNullException.ThrowIfNull(modelClient);
        _conversationStore = conversationStore ?? throw new ArgumentNullException(nameof(conversationStore));
        _policyPipeline = policyPipeline ?? throw new ArgumentNullException(nameof(policyPipeline));
        _options = options ?? new AgentRuntimeOptions();
        _options.Validate();
        _prompt = DomainQueryPrompt.LoadCurrent();
        _tools = new DomainAgentFunctions(policyPipeline).CreateTools();

        IChatClient governedModelClient = new ModelCallBudgetChatClient(modelClient);
        if (modelCallAuditStore is not null)
        {
            governedModelClient = new ModelCallAuditChatClient(governedModelClient, modelCallAuditStore, modelPricing);
        }

        var functionInvokingClient = new FunctionInvokingChatClient(governedModelClient)
        {
            AllowConcurrentInvocation = false,
            IncludeDetailedErrors = false,
            MaximumConsecutiveErrorsPerRequest = 0,
            MaximumIterationsPerRequest = _options.MaxModelCalls,
            TerminateOnUnknownCalls = true
        };
        var chatOptions = new ChatOptions
        {
            Instructions = _prompt.Content,
            Tools = [.. _tools],
            AllowMultipleToolCalls = true
        };
        _chatClientAgent = new ChatClientAgent(
            functionInvokingClient,
            new ChatClientAgentOptions
            {
                Id = "fdd-domain-query-agent",
                Name = "FddDomainQueryAgent",
                Description = "Read-only person, company, relationship, and seal query agent.",
                ChatOptions = chatOptions,
                UseProvidedChatClientAsIs = true
            });

        var builder = new AIAgentBuilder(_chatClientAgent);
        builder.Use(ValidateAgentRunAsync);
        FunctionInvocationDelegatingAgentBuilderExtensions.Use(builder, InvokeFunctionAsync);
        _agentPipeline = builder.Build(services: null);
    }

    public string PromptVersion => _prompt.Version;

    public string PromptSha256 => _prompt.Sha256;

    public ChatClientAgent ChatClientAgent => _chatClientAgent;

    public IReadOnlyList<AIFunction> ToolDefinitions => _tools;

    public IAsyncEnumerable<AgentEvent> RunAsync(
        AgentTurnRequest request,
        CancellationToken cancellationToken)
    {
        var channel = Channel.CreateUnbounded<AgentEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
        _ = ProduceStreamingEventsAsync(request, channel.Writer, cancellationToken);
        return channel.Reader.ReadAllAsync(cancellationToken);
    }

    public async ValueTask<AgentTurnResult> RunOnceAsync(
        AgentTurnRequest request,
        CancellationToken cancellationToken)
    {
        ValidateRequest(request);
        using var activity = AgentTelemetry.StartTurn(request);
        var stopwatch = Stopwatch.StartNew();
        var telemetryStatus = "failed";
        var messages = await LoadCanonicalMessagesAsync(request, cancellationToken).ConfigureAwait(false);
        var context = new AgentTurnContext(request, _options, eventWriter: null);

        try
        {
            using var scope = AgentTurnContextAccessor.Push(context);
            var session = await _chatClientAgent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
            var response = await _agentPipeline.RunAsync(
                messages,
                session,
                new ChatClientAgentRunOptions(),
                cancellationToken).ConfigureAwait(false);
            EnsureCompletedResponse(response.Messages.LastOrDefault()?.Contents ?? [], context);
            telemetryStatus = "succeeded";
            return new AgentTurnResult(
                response.Text,
                context.ModelCalls,
                context.DomainToolCalls,
                _prompt.Version);
        }
        finally
        {
            stopwatch.Stop();
            activity?.SetTag("agent.turn.status", telemetryStatus);
            AgentTelemetry.RecordTurn(stopwatch.Elapsed, telemetryStatus);
            _policyPipeline.ReleaseTurn(request.TurnId);
        }
    }

    private async Task ProduceStreamingEventsAsync(
        AgentTurnRequest request,
        ChannelWriter<AgentEvent> writer,
        CancellationToken cancellationToken)
    {
        using var activity = AgentTelemetry.StartTurn(request);
        var stopwatch = Stopwatch.StartNew();
        var telemetryStatus = "failed";
        try
        {
            ValidateRequest(request);
            await writer.WriteAsync(
                new AgentEvent(AgentEventKind.TurnStarted, null, null, null),
                cancellationToken).ConfigureAwait(false);
            var messages = await LoadCanonicalMessagesAsync(request, cancellationToken).ConfigureAwait(false);
            var context = new AgentTurnContext(request, _options, writer);
            var hasUnmatchedFunctionCall = false;

            using (AgentTurnContextAccessor.Push(context))
            {
                var session = await _chatClientAgent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
                await foreach (var update in _agentPipeline.RunStreamingAsync(
                    messages,
                    session,
                    new ChatClientAgentRunOptions(),
                    cancellationToken).ConfigureAwait(false))
                {
                    if (update.Contents.OfType<FunctionCallContent>().Any())
                    {
                        hasUnmatchedFunctionCall = true;
                    }

                    if (!string.IsNullOrEmpty(update.Text))
                    {
                        hasUnmatchedFunctionCall = false;
                        await writer.WriteAsync(
                            new AgentEvent(AgentEventKind.TextDelta, update.Text, null, null),
                            cancellationToken).ConfigureAwait(false);
                    }
                }
            }

            EnsureCompletedResponse(
                hasUnmatchedFunctionCall ? [new FunctionCallContent("unmatched", "unmatched")] : [],
                context);
            await writer.WriteAsync(
                new AgentEvent(
                    AgentEventKind.TurnCompleted,
                    null,
                    null,
                    null,
                    context.ModelCalls,
                    context.DomainToolCalls),
                cancellationToken).ConfigureAwait(false);
            telemetryStatus = "succeeded";
            writer.TryComplete();
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
        {
            telemetryStatus = "cancelled";
            writer.TryComplete(exception);
        }
        catch (AgentRuntimeException exception)
        {
            telemetryStatus = "rejected";
            writer.TryWrite(new AgentEvent(AgentEventKind.TurnFailed, null, null, exception.ErrorCode));
            writer.TryComplete();
        }
        catch
        {
            writer.TryWrite(new AgentEvent(AgentEventKind.TurnFailed, null, null, "AGENT_RUN_FAILED"));
            writer.TryComplete();
        }
        finally
        {
            stopwatch.Stop();
            activity?.SetTag("agent.turn.status", telemetryStatus);
            AgentTelemetry.RecordTurn(stopwatch.Elapsed, telemetryStatus);
            _policyPipeline.ReleaseTurn(request.TurnId);
        }
    }

    private async ValueTask<IReadOnlyList<ChatMessage>> LoadCanonicalMessagesAsync(
        AgentTurnRequest request,
        CancellationToken cancellationToken)
    {
        var snapshot = await _conversationStore
            .GetAsync(request.ConversationId, request.UserId, cancellationToken)
            .ConfigureAwait(false);
        if (snapshot is null || snapshot.Conversation.Id != request.ConversationId || snapshot.Conversation.UserId != request.UserId)
        {
            throw new AgentRuntimeException("AUTH_CONVERSATION_NOT_FOUND");
        }

        var existingCurrentMessage = snapshot.Messages.SingleOrDefault(message => message.Id == request.UserMessageId);
        if (existingCurrentMessage is not null &&
            (existingCurrentMessage.Role != MessageRole.User ||
             !string.Equals(existingCurrentMessage.Content, request.UserMessage, StringComparison.Ordinal)))
        {
            throw new AgentRuntimeException("AGENT_CANONICAL_MESSAGE_CONFLICT");
        }

        var messages = snapshot.Messages
            .OrderBy(message => message.SequenceNumber)
            .Select(ToChatMessage)
            .ToList();
        if (existingCurrentMessage is null)
        {
            messages.Add(new ChatMessage(ChatRole.User, request.UserMessage));
        }

        return messages;
    }

    private static ChatMessage ToChatMessage(ConversationMessage message) =>
        new(message.Role == MessageRole.User ? ChatRole.User : ChatRole.Assistant, message.Content);

    private static void ValidateRequest(AgentTurnRequest request)
    {
        if (request.TurnId.Value == Guid.Empty ||
            request.ConversationId.Value == Guid.Empty ||
            request.UserId.Value == Guid.Empty ||
            request.UserMessageId.Value == Guid.Empty ||
            request.TraceId == Guid.Empty ||
            string.IsNullOrWhiteSpace(request.UserMessage))
        {
            throw new AgentRuntimeException("AGENT_INVALID_TURN_REQUEST");
        }

    }

    private static void EnsureCompletedResponse(IEnumerable<AIContent> contents, AgentTurnContext context)
    {
        if (contents.OfType<FunctionCallContent>().Any())
        {
            throw new AgentRuntimeException(
                context.ModelCalls >= context.Options.MaxModelCalls
                    ? "AGENT_MODEL_CALL_BUDGET_EXCEEDED"
                    : "AGENT_UNMATCHED_FUNCTION_CALL");
        }
    }

    private static async Task ValidateAgentRunAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        AgentRunOptions? options,
        Func<IEnumerable<ChatMessage>, AgentSession?, AgentRunOptions?, CancellationToken, Task> next,
        CancellationToken cancellationToken)
    {
        _ = AgentTurnContextAccessor.Current;
        if (session is not ChatClientAgentSession chatSession || !string.IsNullOrEmpty(chatSession.ConversationId))
        {
            throw new AgentRuntimeException("AGENT_PROVIDER_SESSION_REJECTED");
        }

        await next(messages, session, options, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<object?> InvokeFunctionAsync(
        AIAgent agent,
        FunctionInvocationContext invocation,
        Func<FunctionInvocationContext, CancellationToken, ValueTask<object?>> next,
        CancellationToken cancellationToken)
    {
        _ = agent;
        var context = AgentTurnContextAccessor.Current;
        var toolName = invocation.Function.Name;
        if (!context.TryBeginDomainTool())
        {
            context.Emit(new AgentEvent(
                AgentEventKind.ToolCompleted,
                null,
                toolName,
                "POLICY_TOOL_BUDGET_EXCEEDED"));
            return JsonSerializer.Serialize(
                new { status = "ToolRejected", errorCode = "POLICY_TOOL_BUDGET_EXCEEDED" },
                JsonOptions);
        }

        context.CurrentToolCallId = ToolCallId.New();
        context.Emit(new AgentEvent(AgentEventKind.ToolStarted, null, toolName, null));
        try
        {
            var result = await next(invocation, cancellationToken).ConfigureAwait(false);
            context.Emit(new AgentEvent(AgentEventKind.ToolCompleted, null, toolName, null));
            return result;
        }
        catch
        {
            context.Emit(new AgentEvent(AgentEventKind.ToolCompleted, null, toolName, "POLICY_TOOL_EXECUTION_FAILED"));
            throw;
        }
        finally
        {
            context.CurrentToolCallId = default;
        }
    }
}
