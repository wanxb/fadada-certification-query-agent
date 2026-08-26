// Defines the application-facing turn protocol without exposing provider-specific agent types.
using Fadada.CertificationQueryAgent.Application.Common;

namespace Fadada.CertificationQueryAgent.Application.AgentTurns;

/// <summary>
/// 定义单轮 Agent 执行的硬预算上限，防止模型或工具发生无界循环调用。
/// </summary>
public static class AgentExecutionLimits
{
    public const int MaximumModelCallsPerTurn = 4;
    public const int MaximumDomainToolCallsPerTurn = 3;
}

/// <summary>
/// 承载 AgentTurnRequest 的已验证输入，限制跨层调用只能使用明确的数据契约。
/// </summary>
public sealed record AgentTurnRequest(
    TurnId TurnId,
    ConversationId ConversationId,
    UserId UserId,
    MessageId UserMessageId,
    string UserMessage,
    Guid TraceId);

/// <summary>
/// 封装 AgentTurnResult 的确定性结果和安全状态，供上层在不解析自由文本的情况下处理。
/// </summary>
public sealed record AgentTurnResult(
    string Text,
    int ModelCalls,
    int DomainToolCalls,
    string PromptVersion);

/// <summary>
/// 定义 AgentEventKind 的受控状态集合，避免跨层使用未校验的自由文本。
/// </summary>
public enum AgentEventKind
{
    TurnStarted,
    Clarification,
    ToolStarted,
    ToolCompleted,
    TextDelta,
    TurnCompleted,
    TurnFailed
}

/// <summary>
/// 以不可变契约保存 AgentEvent 的关键状态，支持审计、恢复和确定性处理。
/// </summary>
public sealed record AgentEvent(
    AgentEventKind Kind,
    string? Text,
    string? ToolName,
    string? SafeErrorCode,
    int? ModelCalls = null,
    int? DomainToolCalls = null);

/// <summary>
/// 定义 IAgentRuntime 的稳定端口，使应用逻辑不依赖具体基础设施并便于替换测试实现。
/// </summary>
public interface IAgentRuntime
{
    IAsyncEnumerable<AgentEvent> RunAsync(
        AgentTurnRequest request,
        CancellationToken cancellationToken);

    ValueTask<AgentTurnResult> RunOnceAsync(
        AgentTurnRequest request,
        CancellationToken cancellationToken);
}

/// <summary>
/// 以不可变契约保存 ModelMessage 的关键状态，支持审计、恢复和确定性处理。
/// </summary>
public sealed record ModelMessage(string Role, string Content);

/// <summary>
/// 以不可变数据契约表达 ModelToolDefinition，确保跨层传递时字段语义和边界保持稳定。
/// </summary>
public sealed record ModelToolDefinition(string Name, string Description, string JsonSchema);

/// <summary>
/// 承载 ModelRunRequest 的已验证输入，限制跨层调用只能使用明确的数据契约。
/// </summary>
public sealed record ModelRunRequest(
    IReadOnlyList<ModelMessage> Messages,
    IReadOnlyList<ModelToolDefinition> Tools,
    string ModelProfile,
    int Attempt);

/// <summary>
/// 以不可变契约保存 ModelRunEvent 的关键状态，支持审计、恢复和确定性处理。
/// </summary>
public sealed record ModelRunEvent(
    string Kind,
    string? Text,
    string? ToolName,
    string? ArgumentsJson,
    int? InputTokens,
    int? OutputTokens);

/// <summary>
/// 定义 IModelRuntime 的稳定端口，使应用逻辑不依赖具体基础设施并便于替换测试实现。
/// </summary>
public interface IModelRuntime
{
    IAsyncEnumerable<ModelRunEvent> CompleteStreamingAsync(
        ModelRunRequest request,
        CancellationToken cancellationToken);
}
