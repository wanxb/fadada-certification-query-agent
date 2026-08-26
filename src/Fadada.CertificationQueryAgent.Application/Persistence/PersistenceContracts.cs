// Persistence ports separate protected session state and diagnostics from ordinary conversation records.
using Fadada.CertificationQueryAgent.Application.Common;
using Fadada.CertificationQueryAgent.Application.Conversations;

namespace Fadada.CertificationQueryAgent.Application.Persistence;

/// <summary>
/// 以不可变契约保存 SessionState 的关键状态，支持审计、恢复和确定性处理。
/// </summary>
public sealed record SessionState(
    ConversationId ConversationId,
    string Format,
    string Version,
    byte[] ProtectedPayload,
    byte[] RowVersion);

/// <summary>
/// 表示 DiagnosticPayload 的基础设施传输形状，仅用于适配外部数据且不作为应用层契约。
/// </summary>
public sealed record DiagnosticPayload(
    Guid Id,
    UserId UserId,
    string OwnerType,
    Guid OwnerId,
    byte[] ProtectedPayload,
    DateTimeOffset ExpiresAtUtc);

/// <summary>
/// 定义 AgentTurnStatus 的受控状态集合，避免跨层使用未校验的自由文本。
/// </summary>
public enum AgentTurnStatus
{
    Started,
    Succeeded,
    Rejected,
    Failed,
    Cancelled
}

/// <summary>
/// 以不可变数据契约表达 AgentTurnStart，确保跨层传递时字段语义和边界保持稳定。
/// </summary>
public sealed record AgentTurnStart(
    TurnId TurnId,
    ConversationId ConversationId,
    UserId UserId,
    Guid TraceId,
    ConversationMessage UserMessage,
    string PromptVersion,
    string PromptSha256,
    string ModelProfile,
    string ToolSetVersion,
    byte[] ExpectedConversationRowVersion,
    DateTimeOffset StartedAtUtc);

/// <summary>
/// 以不可变数据契约表达 AgentTurnCompletion，确保跨层传递时字段语义和边界保持稳定。
/// </summary>
public sealed record AgentTurnCompletion(
    TurnId TurnId,
    ConversationId ConversationId,
    UserId UserId,
    AgentTurnStatus Status,
    ConversationMessage? AssistantMessage,
    int ModelCallCount,
    int ToolCallCount,
    int InputTokens,
    int OutputTokens,
    decimal EstimatedCost,
    string? SafeErrorCode,
    byte[] ExpectedConversationRowVersion,
    DateTimeOffset CompletedAtUtc);

/// <summary>
/// 定义 IAgentTurnStore 的稳定端口，使应用逻辑不依赖具体基础设施并便于替换测试实现。
/// </summary>
public interface IAgentTurnStore
{
    ValueTask<byte[]> StartAsync(AgentTurnStart turn, CancellationToken cancellationToken);

    ValueTask<byte[]> CompleteAsync(AgentTurnCompletion turn, CancellationToken cancellationToken);
}

/// <summary>
/// 表示 PersistenceConcurrencyException 对应边界的稳定失败，只允许上层消费安全错误码。
/// </summary>
public sealed class PersistenceConcurrencyException(string errorCode) : Exception(errorCode)
{
    public string ErrorCode { get; } = errorCode;
}

/// <summary>
/// 定义 IAgentSessionStateStore 的稳定端口，使应用逻辑不依赖具体基础设施并便于替换测试实现。
/// </summary>
public interface IAgentSessionStateStore
{
    ValueTask<SessionState?> GetAsync(
        ConversationId conversationId,
        UserId userId,
        CancellationToken cancellationToken);

    ValueTask SaveAsync(
        SessionState state,
        UserId userId,
        CancellationToken cancellationToken);
}

/// <summary>
/// 定义 IDiagnosticPayloadStore 的稳定端口，使应用逻辑不依赖具体基础设施并便于替换测试实现。
/// </summary>
public interface IDiagnosticPayloadStore
{
    ValueTask SaveAsync(DiagnosticPayload payload, CancellationToken cancellationToken);

    ValueTask<DiagnosticPayload?> GetAsync(
        Guid payloadId,
        UserId userId,
        CancellationToken cancellationToken);

    ValueTask<int> DeleteExpiredAsync(
        DateTimeOffset expiresBeforeUtc,
        int batchSize,
        CancellationToken cancellationToken);
}

/// <summary>
/// 集中表达 DiagnosticCaptureOptions 的配置和约束，使默认值、验证规则与运行行为保持一致。
/// </summary>
public sealed record DiagnosticCaptureOptions(
    bool Enabled = false,
    TimeSpan? TimeToLive = null,
    int MaximumPayloadBytes = 900_000)
{
    public TimeSpan EffectiveTimeToLive => TimeToLive ?? TimeSpan.FromDays(7);

    public void Validate()
    {
        if (EffectiveTimeToLive <= TimeSpan.Zero || EffectiveTimeToLive > TimeSpan.FromDays(7) ||
            MaximumPayloadBytes is < 1 or > 900_000)
        {
            throw new InvalidOperationException("DIAGNOSTIC_CAPTURE_OPTIONS_INVALID");
        }
    }
}

/// <summary>
/// 定义 IDiagnosticCaptureService 的稳定端口，使应用逻辑不依赖具体基础设施并便于替换测试实现。
/// </summary>
public interface IDiagnosticCaptureService
{
    ValueTask<Guid?> CaptureAsync(
        UserId userId,
        string ownerType,
        Guid ownerId,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken);

    ValueTask<byte[]?> ReadAsync(
        Guid payloadId,
        UserId userId,
        CancellationToken cancellationToken);
}

/// <summary>
/// 集中表达 DataLifecycleOptions 的配置和约束，使默认值、验证规则与运行行为保持一致。
/// </summary>
public sealed record DataLifecycleOptions(
    TimeSpan? ArchivedConversationRetention = null,
    TimeSpan? RunInterval = null,
    int BatchSize = 500)
{
    public TimeSpan EffectiveArchivedConversationRetention =>
        ArchivedConversationRetention ?? TimeSpan.FromDays(180);

    public TimeSpan EffectiveRunInterval => RunInterval ?? TimeSpan.FromDays(1);

    public void Validate()
    {
        if (EffectiveArchivedConversationRetention < TimeSpan.FromDays(1) ||
            EffectiveRunInterval < TimeSpan.FromMinutes(1) ||
            BatchSize is < 1 or > 1000)
        {
            throw new InvalidOperationException("DATA_LIFECYCLE_OPTIONS_INVALID");
        }
    }
}

/// <summary>
/// 承载 MaintenanceCleanupRequest 的已验证输入，限制跨层调用只能使用明确的数据契约。
/// </summary>
public sealed record MaintenanceCleanupRequest(
    Guid RunId,
    DateTimeOffset DiagnosticExpiryCutoffUtc,
    DateTimeOffset ArchivedConversationCutoffUtc,
    int BatchSize,
    DateTimeOffset StartedAtUtc);

/// <summary>
/// 封装 MaintenanceCleanupResult 的确定性结果和安全状态，供上层在不解析自由文本的情况下处理。
/// </summary>
public sealed record MaintenanceCleanupResult(
    int DiagnosticPayloadsDeleted,
    int SessionStatesDeleted,
    int MessagesDeleted)
{
    public int TotalDeleted => DiagnosticPayloadsDeleted + SessionStatesDeleted + MessagesDeleted;
}

/// <summary>
/// 定义 IDataLifecycleStore 的稳定端口，使应用逻辑不依赖具体基础设施并便于替换测试实现。
/// </summary>
public interface IDataLifecycleStore
{
    ValueTask<MaintenanceCleanupResult> CleanupAsync(
        MaintenanceCleanupRequest request,
        CancellationToken cancellationToken);
}
