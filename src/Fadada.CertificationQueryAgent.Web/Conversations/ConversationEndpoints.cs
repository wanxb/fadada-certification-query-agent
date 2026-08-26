// Exposes user-scoped conversation APIs and publishes terminal SSE events only after durable completion.
using System.Text.Json;
using Fadada.CertificationQueryAgent.Application.AgentTurns;
using Fadada.CertificationQueryAgent.Application.Common;
using Fadada.CertificationQueryAgent.Application.Conversations;
using Fadada.CertificationQueryAgent.Application.Persistence;
using Fadada.CertificationQueryAgent.AgentHost.Runtime;
using Fadada.CertificationQueryAgent.Web.Security;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;

namespace Fadada.CertificationQueryAgent.Web.Conversations;

/// <summary>
/// 注册会话、归档、恢复和 Agent 回合 API，并强制基于当前用户执行所有权隔离。
/// </summary>
public static class ConversationEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapConversationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/conversations");
        group.MapGet("/", ListAsync);
        group.MapPost("/", CreateAsync).WithMetadata(new RequireAntiforgeryTokenAttribute(true));
        group.MapGet("/{id:guid}", GetAsync);
        group.MapPost("/{id:guid}/archive", ArchiveAsync).WithMetadata(new RequireAntiforgeryTokenAttribute(true));
        group.MapPost("/{id:guid}/restore", RestoreAsync).WithMetadata(new RequireAntiforgeryTokenAttribute(true));
        group.MapPost("/{id:guid}/turns", RunTurnAsync)
            .WithMetadata(new RequireAntiforgeryTokenAttribute(true))
            .RequireRateLimiting("turn");
        return endpoints;
    }

    private static async Task<IResult> ListAsync(
        HttpContext context,
        IConversationStore store,
        CancellationToken cancellationToken)
    {
        if (!WebSecurityRegistration.TryGetUserId(context.User, out var userId))
        {
            return Results.Unauthorized();
        }

        var statusValue = context.Request.Query["status"].ToString();
        var status = string.IsNullOrWhiteSpace(statusValue)
            ? ConversationStatus.Active
            : Enum.TryParse<ConversationStatus>(statusValue, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed)
                ? parsed
                : (ConversationStatus?)null;
        if (status is null)
        {
            return SafeError(StatusCodes.Status400BadRequest, "CONVERSATION_STATUS_INVALID", context.TraceIdentifier);
        }

        var values = await store.ListAsync(userId, status.Value, cancellationToken).ConfigureAwait(false);
        return Results.Ok(values.Select(ToSummary));
    }

    private static async Task<IResult> CreateAsync(
        CreateConversationRequest request,
        HttpContext context,
        IConversationStore store,
        CancellationToken cancellationToken)
    {
        if (!WebSecurityRegistration.TryGetUserId(context.User, out var userId))
        {
            return Results.Unauthorized();
        }

        var title = request.Title?.Trim();
        if (string.IsNullOrWhiteSpace(title) || title.Length > 200)
        {
            return SafeError(StatusCodes.Status400BadRequest, "CONVERSATION_TITLE_INVALID", context.TraceIdentifier);
        }

        var created = await store.CreateAsync(userId, title, cancellationToken).ConfigureAwait(false);
        return Results.Created($"/api/v1/conversations/{created.Id.Value:D}", ToSummary(created));
    }

    private static async Task<IResult> GetAsync(
        Guid id,
        HttpContext context,
        IConversationStore store,
        CancellationToken cancellationToken)
    {
        if (!WebSecurityRegistration.TryGetUserId(context.User, out var userId))
        {
            return Results.Unauthorized();
        }

        var snapshot = await store.GetAsync(new ConversationId(id), userId, cancellationToken).ConfigureAwait(false);
        return snapshot is null
            ? Results.NotFound()
            : Results.Ok(new
            {
                conversation = ToSummary(snapshot.Conversation),
                messages = snapshot.Messages.Select(message => new
                {
                    id = message.Id.Value,
                    role = message.Role.ToString().ToLowerInvariant(),
                    content = message.Content,
                    sequenceNumber = message.SequenceNumber,
                    createdAtUtc = message.CreatedAtUtc
                })
            });
    }

    private static async Task<IResult> ArchiveAsync(
        Guid id,
        HttpContext context,
        IConversationStore store,
        CancellationToken cancellationToken)
    {
        if (!WebSecurityRegistration.TryGetUserId(context.User, out var userId))
        {
            return Results.Unauthorized();
        }

        return await store.ArchiveAsync(new ConversationId(id), userId, cancellationToken).ConfigureAwait(false)
            ? Results.NoContent()
            : SafeError(StatusCodes.Status404NotFound, "CONVERSATION_STATE_CHANGED", context.TraceIdentifier);
    }

    private static async Task<IResult> RestoreAsync(
        Guid id,
        HttpContext context,
        IConversationStore store,
        CancellationToken cancellationToken)
    {
        if (!WebSecurityRegistration.TryGetUserId(context.User, out var userId))
        {
            return Results.Unauthorized();
        }

        return await store.RestoreAsync(new ConversationId(id), userId, cancellationToken).ConfigureAwait(false)
            ? Results.NoContent()
            : SafeError(StatusCodes.Status404NotFound, "CONVERSATION_STATE_CHANGED", context.TraceIdentifier);
    }

    private static async Task RunTurnAsync(
        Guid id,
        RunTurnRequest request,
        HttpContext context,
        IConversationStore conversationStore,
        IAgentTurnStore turnStore,
        IAgentRuntime runtime,
        IConfiguration configuration)
    {
        if (!WebSecurityRegistration.TryGetUserId(context.User, out var userId))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        var messageText = request.Message?.Trim();
        if (string.IsNullOrWhiteSpace(messageText) || messageText.Length > 4000)
        {
            await WriteSafeJsonErrorAsync(context, StatusCodes.Status400BadRequest, "AGENT_MESSAGE_INVALID").ConfigureAwait(false);
            return;
        }

        var conversationId = new ConversationId(id);
        var snapshot = await conversationStore.GetAsync(conversationId, userId, context.RequestAborted).ConfigureAwait(false);
        if (snapshot is null || snapshot.Conversation.Status != ConversationStatus.Active || snapshot.Conversation.RowVersion is not { Length: 8 })
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var turnId = TurnId.New();
        var userMessageId = MessageId.New();
        var traceId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var nextSequence = snapshot.Messages.Count == 0 ? 1 : snapshot.Messages.Max(message => message.SequenceNumber) + 1;
        var descriptor = runtime as DomainAgentRuntime;
        byte[] currentRowVersion;
        try
        {
            currentRowVersion = await turnStore.StartAsync(
                new AgentTurnStart(
                    turnId,
                    conversationId,
                    userId,
                    traceId,
                    new ConversationMessage(
                        userMessageId,
                        conversationId,
                        turnId,
                        MessageRole.User,
                        messageText,
                        nextSequence,
                        now),
                    descriptor?.PromptVersion ?? "query-agent.v2",
                    descriptor?.PromptSha256 ?? new string('0', 64),
                    configuration["Model:Name"] ?? "configured-model",
                    "domain-tools.v1",
                    snapshot.Conversation.RowVersion,
                    now),
                context.RequestAborted).ConfigureAwait(false);
        }
        catch (PersistenceConcurrencyException)
        {
            await WriteSafeJsonErrorAsync(context, StatusCodes.Status409Conflict, "STORE_CONVERSATION_CONFLICT").ConfigureAwait(false);
            return;
        }
        catch
        {
            await WriteSafeJsonErrorAsync(context, StatusCodes.Status503ServiceUnavailable, "STORE_TURN_START_FAILED").ConfigureAwait(false);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "text/event-stream; charset=utf-8";
        context.Response.Headers.Append("X-Accel-Buffering", "no");
        var answer = new System.Text.StringBuilder();
        AgentTurnStatus completionStatus = AgentTurnStatus.Failed;
        string? safeErrorCode = "AGENT_STREAM_INCOMPLETE";
        var modelCalls = 0;
        var toolCalls = 0;
        AgentEvent? terminalEvent = null;
        try
        {
            var turnRequest = new AgentTurnRequest(turnId, conversationId, userId, userMessageId, messageText, traceId);
            await foreach (var agentEvent in runtime.RunAsync(turnRequest, context.RequestAborted).ConfigureAwait(false))
            {
                if (agentEvent.Kind == AgentEventKind.TextDelta && agentEvent.Text is not null)
                {
                    answer.Append(agentEvent.Text);
                }
                else if (agentEvent.Kind == AgentEventKind.TurnCompleted)
                {
                    completionStatus = AgentTurnStatus.Succeeded;
                    safeErrorCode = null;
                    modelCalls = agentEvent.ModelCalls ?? 0;
                    toolCalls = agentEvent.DomainToolCalls ?? 0;
                    terminalEvent = agentEvent;
                }
                else if (agentEvent.Kind == AgentEventKind.TurnFailed)
                {
                    completionStatus = AgentTurnStatus.Rejected;
                    safeErrorCode = SafeCode(agentEvent.SafeErrorCode);
                    terminalEvent = agentEvent with { SafeErrorCode = safeErrorCode };
                }

                if (agentEvent.Kind is not (AgentEventKind.TurnCompleted or AgentEventKind.TurnFailed))
                {
                    await WriteSseAsync(context, agentEvent, traceId).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            completionStatus = AgentTurnStatus.Cancelled;
            safeErrorCode = "AGENT_CLIENT_CANCELLED";
        }
        catch
        {
            completionStatus = AgentTurnStatus.Failed;
            safeErrorCode = "AGENT_RUN_FAILED";
            terminalEvent = new AgentEvent(AgentEventKind.TurnFailed, null, null, safeErrorCode);
        }

        var assistant = completionStatus == AgentTurnStatus.Succeeded && answer.Length > 0
            ? new ConversationMessage(
                MessageId.New(),
                conversationId,
                turnId,
                MessageRole.Assistant,
                answer.ToString(),
                nextSequence + 1,
                DateTimeOffset.UtcNow)
            : null;
        try
        {
            await turnStore.CompleteAsync(
                new AgentTurnCompletion(
                    turnId,
                    conversationId,
                    userId,
                    completionStatus,
                    assistant,
                    modelCalls,
                    toolCalls,
                    0,
                    0,
                    0,
                    safeErrorCode,
                    currentRowVersion,
                    DateTimeOffset.UtcNow),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            await TryWriteTerminalSseAsync(
                context,
                new AgentEvent(AgentEventKind.TurnFailed, null, null, "STORE_TURN_COMPLETION_FAILED"),
                traceId).ConfigureAwait(false);
            return;
        }

        terminalEvent ??= new AgentEvent(AgentEventKind.TurnFailed, null, null, safeErrorCode);
        await TryWriteTerminalSseAsync(context, terminalEvent, traceId).ConfigureAwait(false);
    }

    private static async Task TryWriteTerminalSseAsync(HttpContext context, AgentEvent value, Guid traceId)
    {
        if (context.RequestAborted.IsCancellationRequested)
        {
            return;
        }

        try
        {
            await WriteSseAsync(context, value, traceId).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
        }
        catch (IOException) when (context.RequestAborted.IsCancellationRequested)
        {
        }
    }

    private static async Task WriteSseAsync(HttpContext context, AgentEvent value, Guid traceId)
    {
        var mapped = value.Kind switch
        {
            AgentEventKind.TurnStarted => ("turn.started", (object)new { traceId }),
            AgentEventKind.TextDelta => ("agent.text.delta", (object)new { text = value.Text ?? string.Empty }),
            AgentEventKind.ToolStarted => ("tool.started", (object)new { toolName = SafeToolName(value.ToolName) }),
            AgentEventKind.ToolCompleted => ("tool.completed", (object)new
            {
                toolName = SafeToolName(value.ToolName),
                status = value.SafeErrorCode is null ? "succeeded" : "failed"
            }),
            AgentEventKind.TurnCompleted => ("turn.completed", (object)new
            {
                modelCalls = value.ModelCalls ?? 0,
                toolCalls = value.DomainToolCalls ?? 0
            }),
            AgentEventKind.TurnFailed => ("turn.failed", (object)new { errorCode = SafeCode(value.SafeErrorCode), traceId }),
            _ => default
        };
        if (mapped == default)
        {
            return;
        }

        await context.Response.WriteAsync($"event: {mapped.Item1}\n", context.RequestAborted).ConfigureAwait(false);
        await context.Response.WriteAsync($"data: {JsonSerializer.Serialize(mapped.Item2, JsonOptions)}\n\n", context.RequestAborted).ConfigureAwait(false);
        await context.Response.Body.FlushAsync(context.RequestAborted).ConfigureAwait(false);
    }

    private static string SafeToolName(string? value) => value is
        "query_person" or "query_company" or "query_relationship" or "query_seals"
            ? value
            : "unknown";

    private static string SafeCode(string? value) =>
        value is { Length: > 0 and <= 64 } && value.All(character => char.IsAsciiLetterUpper(character) || char.IsDigit(character) || character == '_')
            ? value
            : "AGENT_RUN_FAILED";

    private static object ToSummary(ConversationSummary value) => new
    {
        id = value.Id.Value,
        title = value.Title,
        status = value.Status.ToString().ToLowerInvariant(),
        updatedAtUtc = value.UpdatedAtUtc
    };

    private static IResult SafeError(int statusCode, string errorCode, string traceId) =>
        Results.Json(new { errorCode, traceId }, statusCode: statusCode);

    private static Task WriteSafeJsonErrorAsync(HttpContext context, int statusCode, string errorCode)
    {
        context.Response.StatusCode = statusCode;
        return context.Response.WriteAsJsonAsync(new { errorCode, traceId = context.TraceIdentifier });
    }

    /// <summary>
    /// 承载 CreateConversationRequest 的已验证输入，限制跨层调用只能使用明确的数据契约。
    /// </summary>
    public sealed record CreateConversationRequest(string? Title);

    /// <summary>
    /// 承载 RunTurnRequest 的已验证输入，限制跨层调用只能使用明确的数据契约。
    /// </summary>
    public sealed record RunTurnRequest(string? Message);
}
