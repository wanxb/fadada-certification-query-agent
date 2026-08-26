// Drives the current Agent pipeline with a scripted chat client to isolate architecture behavior.
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fadada.CertificationQueryAgent.AgentHost.Runtime;
using Fadada.CertificationQueryAgent.Application.AgentTurns;
using Fadada.CertificationQueryAgent.Application.Common;
using Fadada.CertificationQueryAgent.Application.Conversations;
using Fadada.CertificationQueryAgent.Application.DomainTools;
using Microsoft.Extensions.AI;

namespace Fadada.CertificationQueryAgent.Evals;

/// <summary>
/// 支撑离线测试中的 OfflineAgentTarget 职责，确保测试过程确定且不访问真实外部系统。
/// </summary>
public sealed class OfflineAgentTarget(DeterministicFadadaFixture fixture) : IEvaluationTarget
{
    private const decimal InputCostPerMillionTokens = 2m;
    private const decimal OutputCostPerMillionTokens = 8m;

    public string Name => "current-agent-maf-offline";

    public string EvaluationMode => "offline-deterministic-pipeline-conformance";

    public bool SupportsModelQualityClaims => false;

    public async ValueTask<EvaluationTargetOutput> ExecuteAsync(
        EvaluationCase scenario,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var timer = Stopwatch.StartNew();
        var authenticatedUserId = new UserId(StableGuid($"user:{scenario.Ownership.AuthenticatedUserId}"));
        var ownerUserId = new UserId(StableGuid($"user:{scenario.Ownership.ConversationOwnerUserId}"));
        var conversationId = new ConversationId(StableGuid($"conversation:{scenario.Id}"));
        var request = new AgentTurnRequest(
            new TurnId(StableGuid($"turn:{scenario.Id}")),
            conversationId,
            authenticatedUserId,
            new MessageId(StableGuid($"message:{scenario.Turns[^1].MessageId}")),
            scenario.Turns[^1].Content,
            StableGuid($"trace:{scenario.Id}"));
        var store = new ScenarioConversationStore(CreateSnapshot(scenario, conversationId, ownerUserId));
        var policy = new RecordingEvaluationPolicy(fixture, scenario.FixtureKey);
        var client = new ScenarioScriptedChatClient(scenario, fixture.Resolve(scenario.FixtureKey));
        var runtime = new DomainAgentRuntime(client, store, policy);

        var evidence = "None";
        var safetyDecisions = new List<string>();
        var clarificationRequested = false;
        try
        {
            var result = await runtime.RunOnceAsync(request, cancellationToken);
            evidence = ParseEvidence(result.Text);
            clarificationRequested = result.Text.StartsWith("clarification:", StringComparison.Ordinal);
        }
        catch (AgentRuntimeException exception) when (exception.ErrorCode == "AUTH_CONVERSATION_NOT_FOUND")
        {
            evidence = "Rejected";
            safetyDecisions.Add("ownership_rejected");
        }

        timer.Stop();
        var inputTokens = client.Calls * ScenarioScriptedChatClient.InputTokensPerCall;
        var outputTokens = client.Calls * ScenarioScriptedChatClient.OutputTokensPerCall;
        var output = new EvaluationTargetOutput(
            clarificationRequested,
            policy.ToolCalls,
            policy.Arguments,
            evidence,
            safetyDecisions,
            client.Calls,
            inputTokens,
            outputTokens,
            EstimateCost(inputTokens, outputTokens),
            timer.ElapsedMilliseconds);
        var standardResults = await DeterministicStandardEvaluators.EvaluateAsync(
            scenario,
            output,
            cancellationToken);
        return output with
        {
            MafEvaluationPassed = standardResults.MafPassed,
            MeaiEvaluationPassed = standardResults.MeaiPassed
        };
    }

    private static ConversationSnapshot CreateSnapshot(
        EvaluationCase scenario,
        ConversationId conversationId,
        UserId ownerUserId)
    {
        var messages = scenario.Turns
            .Take(scenario.Turns.Count - 1)
            .Select((turn, index) => new ConversationMessage(
                new MessageId(StableGuid($"message:{turn.MessageId}")),
                conversationId,
                null,
                MessageRole.User,
                turn.Content,
                index + 1,
                DateTimeOffset.UnixEpoch.AddSeconds(index)))
            .ToArray();
        return new ConversationSnapshot(
            new ConversationSummary(
                conversationId,
                ownerUserId,
                scenario.Id,
                ConversationStatus.Active,
                DateTimeOffset.UnixEpoch),
            messages);
    }

    private static string ParseEvidence(string response) =>
        response.StartsWith("evidence:", StringComparison.Ordinal)
            ? response["evidence:".Length..]
            : response.StartsWith("clarification:", StringComparison.Ordinal) ? "None" : "Invalid";

    private static decimal EstimateCost(int inputTokens, int outputTokens) =>
        decimal.Round(
            (inputTokens * InputCostPerMillionTokens + outputTokens * OutputCostPerMillionTokens) / 1_000_000m,
            8);

    private static Guid StableGuid(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return new Guid(hash.AsSpan(0, 16));
    }

    /// <summary>
    /// 为所属测试提供可观测且无外部副作用的 ScenarioScriptedChatClient 测试替身。
    /// </summary>
    private sealed class ScenarioScriptedChatClient(
        EvaluationCase scenario,
        FixtureResponse fixtureResponse) : IChatClient
    {
        public const int InputTokensPerCall = 24;
        public const int OutputTokensPerCall = 8;

        public int Calls { get; private set; }

        public void Dispose()
        {
        }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType == typeof(ChatClientMetadata)
                ? new ChatClientMetadata("scripted-eval", new Uri("https://eval.invalid"), "offline-scenario-model")
                : serviceType.IsInstanceOfType(this) ? this : null;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            if (scenario.ExpectedClarification)
            {
                return Task.FromResult(Response("clarification:required"));
            }

            if (scenario.ExpectedToolCalls.Count == 0 || Calls > 1)
            {
                return Task.FromResult(Response($"evidence:{fixtureResponse.EvidenceStatus}"));
            }

            var arguments = scenario.ExpectedArguments.ToDictionary(
                pair => pair.Key,
                pair => (object?)pair.Value,
                StringComparer.Ordinal);
            var message = new ChatMessage(
                ChatRole.Assistant,
                [new FunctionCallContent($"scenario-call-{Calls}", scenario.ExpectedToolCalls[0], arguments)]);
            return Task.FromResult(new ChatResponse(message)
            {
                Usage = new UsageDetails
                {
                    InputTokenCount = InputTokensPerCall,
                    OutputTokenCount = OutputTokensPerCall
                }
            });
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken);
            yield return new ChatResponseUpdate(response.Messages[0].Role, response.Messages[0].Contents);
        }

        private static ChatResponse Response(string text) =>
            new(new ChatMessage(ChatRole.Assistant, text))
            {
                Usage = new UsageDetails
                {
                    InputTokenCount = InputTokensPerCall,
                    OutputTokenCount = OutputTokensPerCall
                }
            };
    }

    /// <summary>
    /// 为所属测试提供可观测且无外部副作用的 RecordingEvaluationPolicy 测试替身。
    /// </summary>
    private sealed class RecordingEvaluationPolicy(
        DeterministicFadadaFixture fixture,
        string fixtureKey) : IToolPolicyPipeline
    {
        private readonly Dictionary<string, string?> arguments = new(StringComparer.Ordinal);
        private readonly List<string> toolCalls = [];

        public IReadOnlyDictionary<string, string?> Arguments => arguments;

        public IReadOnlyList<string> ToolCalls => toolCalls;

        public ValueTask<ToolPolicyResult> InvokeAsync(
            ToolInvocationRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            toolCalls.Add(request.ToolName);
            using var document = JsonDocument.Parse(request.ArgumentsJson);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                arguments[property.Name] = property.Value.ValueKind == JsonValueKind.Null
                    ? null
                    : property.Value.GetString();
            }

            var response = fixture.Resolve(fixtureKey);
            var json = JsonSerializer.Serialize(new
            {
                status = response.EvidenceStatus,
                safeErrorCode = response.SafeErrorCode
            });
            return ValueTask.FromResult(new ToolPolicyResult(true, json, null, []));
        }

        public void ReleaseTurn(TurnId turnId)
        {
        }
    }

    /// <summary>
    /// 为所属测试提供可观测且无外部副作用的 ScenarioConversationStore 测试替身。
    /// </summary>
    private sealed class ScenarioConversationStore(ConversationSnapshot snapshot) : IConversationStore
    {
        public ValueTask<ConversationSummary> CreateAsync(
            UserId userId,
            string title,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<ConversationSnapshot?> GetAsync(
            ConversationId conversationId,
            UserId userId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<ConversationSnapshot?>(
                snapshot.Conversation.Id == conversationId && snapshot.Conversation.UserId == userId
                    ? snapshot
                    : null);
        }

        public ValueTask<IReadOnlyList<ConversationSummary>> ListAsync(
            UserId userId,
            ConversationStatus status,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<bool> ArchiveAsync(
            ConversationId conversationId,
            UserId userId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<bool> RestoreAsync(
            ConversationId conversationId,
            UserId userId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
