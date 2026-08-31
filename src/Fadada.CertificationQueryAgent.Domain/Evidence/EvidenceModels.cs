// Evidence records carry normalized facts and integrity labels, never raw provider payloads.
namespace Fadada.CertificationQueryAgent.Domain.Evidence;

/// <summary>
/// 定义 EvidenceStatus 的受控状态集合，避免跨层使用未校验的自由文本。
/// </summary>
public enum EvidenceStatus
{
    Succeeded,
    Partial,
    NotFound,
    Ambiguous,
    Rejected,
    Failed
}

/// <summary>
/// 定义 EvidenceIntegrity 的受控状态集合，避免跨层使用未校验的自由文本。
/// </summary>
public enum EvidenceIntegrity
{
    ExternalUntrusted
}

/// <summary>
/// 定义 FactReliability 的受控状态集合，避免跨层使用未校验的自由文本。
/// </summary>
public enum FactReliability
{
    ReliableIdentifier,
    VerifiedAttribute,
    AuxiliaryAttribute,
    Unverified
}

/// <summary>
/// 定义 BusinessStatus 的受控状态集合，避免跨层使用未校验的自由文本。
/// </summary>
public enum BusinessStatus
{
    Unknown,
    NotFound,
    Unregistered,
    NotVerified,
    InProgress,
    Verified,
    Active,
    Inactive,
    Failed
}

/// <summary>
/// 定义 ConclusionStatus 的受控状态集合，避免跨层使用未校验的自由文本。
/// </summary>
public enum ConclusionStatus
{
    Confirmed,
    Mismatch,
    NotFound,
    NotVerified,
    Partial,
    Unknown,
    Rejected,
    Failed
}

/// <summary>
/// 以规范化结构表达 EvidenceFact，不允许外部提供方原始载荷直接成为领域事实。
/// </summary>
public sealed record EvidenceFact(string Name, string? Value, FactReliability Reliability);

/// <summary>
/// 以不可变数据契约表达 SafeEvidenceError，确保跨层传递时字段语义和边界保持稳定。
/// </summary>
public sealed record SafeEvidenceError(string Code, string Source, bool Retryable);

/// <summary>
/// 以规范化结构表达 EvidenceMetadata，不允许外部提供方原始载荷直接成为领域事实。
/// </summary>
public sealed record EvidenceMetadata(
    DateTimeOffset ObservedAtUtc,
    IReadOnlyList<string> SourceEndpointKeys,
    Guid TraceId,
    EvidenceIntegrity Integrity = EvidenceIntegrity.ExternalUntrusted);

/// <summary>
/// 封装 DeterministicConclusion 的确定性结果和安全状态，供上层在不解析自由文本的情况下处理。
/// </summary>
public sealed record DeterministicConclusion(
    ConclusionStatus Status,
    string Code,
    string Summary);

/// <summary>
/// 表示 EvidenceEnvelope 的基础设施传输形状，仅用于适配外部数据且不作为应用层契约。
/// </summary>
public sealed record EvidenceEnvelope<T>(
    EvidenceStatus Status,
    T? Data,
    IReadOnlyList<EvidenceFact> Facts,
    DeterministicConclusion Conclusion,
    IReadOnlyList<string> MissingEvidence,
    IReadOnlyList<SafeEvidenceError> SafeErrors,
    EvidenceMetadata Metadata);

/// <summary>
/// 以规范化结构表达 PersonEvidence，不允许外部提供方原始载荷直接成为领域事实。
/// </summary>
public sealed record PersonEvidence(
    string? AccountId,
    BusinessStatus AccountStatus,
    BusinessStatus VerificationStatus,
    string? VerifiedName,
    bool? ClaimedNameMatches);

/// <summary>
/// 以规范化结构表达 AdministratorEvidence，不允许外部提供方原始载荷直接成为领域事实。
/// </summary>
public sealed record AdministratorEvidence(
    string? AccountId,
    string? Name,
    string? Mobile);

/// <summary>
/// 以规范化结构表达 CompanyEvidence，不允许外部提供方原始载荷直接成为领域事实。
/// </summary>
public sealed record CompanyEvidence(
    string? CompanyId,
    BusinessStatus CompanyStatus,
    BusinessStatus VerificationStatus,
    AdministratorEvidence? Administrator);

/// <summary>
/// 以规范化结构表达 RelationshipEvidence，不允许外部提供方原始载荷直接成为领域事实。
/// </summary>
public sealed record RelationshipEvidence(
    PersonEvidence Person,
    CompanyEvidence Company,
    bool? AuxiliaryNameMatches,
    bool? AuxiliaryMobileMatches);

/// <summary>
/// 表达可用于回答业务问题的印章授权用户，主动排除仅供系统关联的供应商账号标识。
/// </summary>
public sealed record SealAuthorizedUserEvidence(
    string? UserName,
    string? AreaCode,
    string? Mobile,
    string? Email,
    string? AuthorizedAt,
    string? ValidFrom,
    string? ValidUntil,
    int? UseTimes);

/// <summary>
/// 汇总单枚印章状态及授权证据，以总数和完整性标记防止有限模型上下文产生完整性误判。
/// </summary>
public sealed record SealEvidence(
    string SealId,
    string DisplayName,
    string Type,
    BusinessStatus Status,
    bool? HasAuthorization,
    IReadOnlyList<SealAuthorizedUserEvidence> AuthorizedUsers,
    int? AuthorizedUserCount,
    bool AuthorizedUsersComplete,
    bool AuthorizedUsersTruncated)
{
    /// <summary>
    /// 限制单枚印章进入模型上下文的授权用户数量，与通用工具结果清洗器的数组上限保持一致。
    /// </summary>
    public const int MaximumAuthorizedUsers = 100;
}

/// <summary>
/// 以规范化结构表达 SealsEvidence，不允许外部提供方原始载荷直接成为领域事实。
/// </summary>
public sealed record SealsEvidence(
    CompanyEvidence Company,
    IReadOnlyList<SealEvidence> Seals,
    string? PersonAccountId);
