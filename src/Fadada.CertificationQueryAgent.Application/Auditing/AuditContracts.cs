// Audit ports require a durable prewrite before any model, tool, or external operation starts.
using Fadada.CertificationQueryAgent.Application.Common;

namespace Fadada.CertificationQueryAgent.Application.Auditing;

/// <summary>
/// 定义 AuditOperationStatus 的受控状态集合，避免跨层使用未校验的自由文本。
/// </summary>
public enum AuditOperationStatus
{
    Started,
    Succeeded,
    Rejected,
    Failed,
    Cancelled
}

/// <summary>
/// 定义 AuditOperationKind 的受控状态集合，避免跨层使用未校验的自由文本。
/// </summary>
public enum AuditOperationKind
{
    Tool,
    External
}

/// <summary>
/// 以不可变数据契约表达 AuditPrewrite，确保跨层传递时字段语义和边界保持稳定。
/// </summary>
public sealed record AuditPrewrite(
    Guid AuditId,
    UserId UserId,
    ConversationId ConversationId,
    TurnId TurnId,
    string Operation,
    DateTimeOffset StartedAtUtc,
    string? SafeArgumentsSummary = null,
    ToolCallId? ParentToolCallId = null);

/// <summary>
/// 以不可变数据契约表达 AuditCompletion，确保跨层传递时字段语义和边界保持稳定。
/// </summary>
public sealed record AuditCompletion(
    Guid AuditId,
    AuditOperationKind Kind,
    AuditOperationStatus Status,
    string? SafeErrorCode,
    long DurationMilliseconds,
    DateTimeOffset CompletedAtUtc,
    string? SafeResultSummary = null);

/// <summary>
/// 定义 IAuditStore 的稳定端口，使应用逻辑不依赖具体基础设施并便于替换测试实现。
/// </summary>
public interface IAuditStore
{
    ValueTask PrewriteAsync(AuditPrewrite entry, CancellationToken cancellationToken);

    ValueTask CompleteAsync(AuditCompletion completion, CancellationToken cancellationToken);
}

/// <summary>
/// 以不可变数据契约表达 ModelCallAuditStart，确保跨层传递时字段语义和边界保持稳定。
/// </summary>
public sealed record ModelCallAuditStart(
    Guid Id,
    UserId UserId,
    ConversationId ConversationId,
    TurnId TurnId,
    int AttemptNumber,
    string Provider,
    string ModelName,
    DateTimeOffset StartedAtUtc);

/// <summary>
/// 以不可变数据契约表达 ModelCallAuditCompletion，确保跨层传递时字段语义和边界保持稳定。
/// </summary>
public sealed record ModelCallAuditCompletion(
    Guid Id,
    TurnId TurnId,
    AuditOperationStatus Status,
    int InputTokens,
    int OutputTokens,
    decimal EstimatedCost,
    long DurationMilliseconds,
    string? SafeErrorCode,
    DateTimeOffset CompletedAtUtc);

/// <summary>
/// 以不可变数据契约表达 ModelPricing，确保跨层传递时字段语义和边界保持稳定。
/// </summary>
public sealed record ModelPricing(
    decimal InputCostPerMillionTokens,
    decimal OutputCostPerMillionTokens)
{
    public void Validate()
    {
        if (InputCostPerMillionTokens < 0 || OutputCostPerMillionTokens < 0)
        {
            throw new InvalidOperationException("MODEL_PRICING_INVALID");
        }
    }

    public decimal Estimate(long inputTokens, long outputTokens) =>
        decimal.Round(
            ((inputTokens * InputCostPerMillionTokens) + (outputTokens * OutputCostPerMillionTokens)) / 1_000_000m,
            8,
            MidpointRounding.AwayFromZero);
}

/// <summary>
/// 定义 IModelCallAuditStore 的稳定端口，使应用逻辑不依赖具体基础设施并便于替换测试实现。
/// </summary>
public interface IModelCallAuditStore
{
    ValueTask PrewriteAsync(ModelCallAuditStart entry, CancellationToken cancellationToken);

    ValueTask CompleteAsync(ModelCallAuditCompletion completion, CancellationToken cancellationToken);
}
