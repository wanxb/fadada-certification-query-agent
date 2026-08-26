// Policy labels and ports define the trust transition from user text to an audited tool execution.
using Fadada.CertificationQueryAgent.Application.Common;

namespace Fadada.CertificationQueryAgent.Application.DomainTools;

/// <summary>
/// 定义 IntegrityLabel 的受控状态集合，避免跨层使用未校验的自由文本。
/// </summary>
public enum IntegrityLabel
{
    TrustedSystem,
    UserAuthorized,
    ExternalUntrusted,
    Secret
}

/// <summary>
/// 定义 ProvenanceFieldKind 的受控状态集合，避免跨层使用未校验的自由文本。
/// </summary>
public enum ProvenanceFieldKind
{
    Mobile,
    PersonName,
    CompanyFullName
}

/// <summary>
/// 定义 ConfirmationState 的受控状态集合，避免跨层使用未校验的自由文本。
/// </summary>
public enum ConfirmationState
{
    UserExplicit,
    UserConfirmed,
    Inferred
}

/// <summary>
/// 以不可变数据契约表达 UserProvidedValue，确保跨层传递时字段语义和边界保持稳定。
/// </summary>
public sealed record UserProvidedValue(
    UserId UserId,
    ConversationId ConversationId,
    MessageId MessageId,
    ProvenanceFieldKind FieldKind,
    string OriginalValue,
    string CanonicalValue,
    ConfirmationState ConfirmationState,
    DateTimeOffset ObservedAtUtc,
    IntegrityLabel Integrity = IntegrityLabel.UserAuthorized);

/// <summary>
/// 以不可变数据契约表达 ProvenanceCandidate，确保跨层传递时字段语义和边界保持稳定。
/// </summary>
public sealed record ProvenanceCandidate(
    ProvenanceFieldKind FieldKind,
    string Value);

/// <summary>
/// 承载 ToolInvocationRequest 的已验证输入，限制跨层调用只能使用明确的数据契约。
/// </summary>
public sealed record ToolInvocationRequest(
    UserId? UserId,
    ConversationId ConversationId,
    TurnId TurnId,
    ToolCallId ToolCallId,
    Guid TraceId,
    string ToolName,
    string ArgumentsJson);

/// <summary>
/// 承载 ToolExecutionRequest 的已验证输入，限制跨层调用只能使用明确的数据契约。
/// </summary>
public sealed record ToolExecutionRequest(
    DomainQueryContext Context,
    string ToolName,
    IReadOnlyDictionary<string, string> CanonicalArguments);

/// <summary>
/// 封装 ToolExecutionResult 的确定性结果和安全状态，供上层在不解析自由文本的情况下处理。
/// </summary>
public sealed record ToolExecutionResult(string Json, IntegrityLabel Integrity);

/// <summary>
/// 封装 ToolPolicyDecision 的确定性结果和安全状态，供上层在不解析自由文本的情况下处理。
/// </summary>
public sealed record ToolPolicyDecision(string PolicyId, bool Allowed, string? ErrorCode);

/// <summary>
/// 封装 ToolPolicyResult 的确定性结果和安全状态，供上层在不解析自由文本的情况下处理。
/// </summary>
public sealed record ToolPolicyResult(
    bool Allowed,
    string? SanitizedResultJson,
    string? ErrorCode,
    IReadOnlyList<ToolPolicyDecision> Decisions);

/// <summary>
/// 定义 IConversationOwnershipVerifier 的稳定端口，使应用逻辑不依赖具体基础设施并便于替换测试实现。
/// </summary>
public interface IConversationOwnershipVerifier
{
    ValueTask<bool> IsOwnerAsync(
        ConversationId conversationId,
        UserId userId,
        CancellationToken cancellationToken);
}

/// <summary>
/// 定义 IUserProvenanceStore 的稳定端口，使应用逻辑不依赖具体基础设施并便于替换测试实现。
/// </summary>
public interface IUserProvenanceStore
{
    ValueTask<IReadOnlyList<UserProvidedValue>> ResolveAsync(
        ConversationId conversationId,
        UserId userId,
        IReadOnlyCollection<ProvenanceCandidate> candidates,
        CancellationToken cancellationToken);
}

/// <summary>
/// 定义 IRegisteredToolExecutor 的稳定端口，使应用逻辑不依赖具体基础设施并便于替换测试实现。
/// </summary>
public interface IRegisteredToolExecutor
{
    ValueTask<ToolExecutionResult> ExecuteAsync(
        ToolExecutionRequest request,
        CancellationToken cancellationToken);
}

/// <summary>
/// 定义 IToolPolicyPipeline 的稳定端口，使应用逻辑不依赖具体基础设施并便于替换测试实现。
/// </summary>
public interface IToolPolicyPipeline
{
    ValueTask<ToolPolicyResult> InvokeAsync(
        ToolInvocationRequest request,
        CancellationToken cancellationToken);

    void ReleaseTurn(TurnId turnId);
}
