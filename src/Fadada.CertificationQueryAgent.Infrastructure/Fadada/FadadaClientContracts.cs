// Internal transport results distinguish not-found evidence from safe retryable provider failures.
using Fadada.CertificationQueryAgent.Application.DomainTools;
using Fadada.CertificationQueryAgent.Domain.Evidence;

namespace Fadada.CertificationQueryAgent.Infrastructure.Fadada;

/// <summary>
/// 封装 FadadaResult 的确定性结果和安全状态，供上层在不解析自由文本的情况下处理。
/// </summary>
internal sealed record FadadaResult<T>(T? Value, EvidenceStatus Status, SafeEvidenceError? Error)
{
    public bool IsSuccess => Status == EvidenceStatus.Succeeded;

    public static FadadaResult<T> Success(T value) => new(value, EvidenceStatus.Succeeded, null);

    public static FadadaResult<T> NotFound() => new(default, EvidenceStatus.NotFound, null);

    public static FadadaResult<T> Failure(string code, string source, bool retryable = false) =>
        new(default, EvidenceStatus.Failed, new SafeEvidenceError(code, source, retryable));
}

/// <summary>
/// 表示 AccountRecord 的基础设施传输形状，仅用于适配外部数据且不作为应用层契约。
/// </summary>
internal sealed record AccountRecord(string AccountId, string Mobile, BusinessStatus Status);

/// <summary>
/// 表示 PersonVerificationRecord 的基础设施传输形状，仅用于适配外部数据且不作为应用层契约。
/// </summary>
internal sealed record PersonVerificationRecord(string AccountId, string? VerifiedName, BusinessStatus Status);

/// <summary>
/// 表示 AdministratorRecord 的基础设施传输形状，仅用于适配外部数据且不作为应用层契约。
/// </summary>
internal sealed record AdministratorRecord(string? AccountId, string? Name, string? Mobile);

/// <summary>
/// 表示 CompanyRecord 的基础设施传输形状，仅用于适配外部数据且不作为应用层契约。
/// </summary>
internal sealed record CompanyRecord(
    string CompanyId,
    string CompanyFullName,
    BusinessStatus Status,
    AdministratorRecord? Administrator);

/// <summary>
/// 表示 SealRecord 的基础设施传输形状，仅用于适配外部数据且不作为应用层契约。
/// </summary>
internal sealed record SealRecord(string SealId, string Name, string Type, BusinessStatus Status);

/// <summary>
/// 规范化法大大印章授权用户信息，隔离供应商字段别名与不稳定的日期格式。
/// </summary>
internal sealed record SealAuthorizedUserRecord(
    string? AccountId,
    string? ThirdPartyAccountId,
    string? UserName,
    string? AreaCode,
    string? Mobile,
    string? Email,
    string? AuthorizedAt,
    string? ValidFrom,
    string? ValidUntil,
    int? UseTimes);

/// <summary>
/// 承载授权用户解析结果并记录集合是否完整，使字段缺失或脏数据不会被误判为合法空列表。
/// </summary>
internal sealed record SealAuthorizedUserCollection(
    IReadOnlyList<SealAuthorizedUserRecord> Users,
    bool IsComplete);

/// <summary>
/// 汇总印章详情及授权用户，保留列表完整性和旧版账号标识以支持可靠的上层结论。
/// </summary>
internal sealed record SealInfoRecord(
    string SealId,
    string Name,
    string Type,
    BusinessStatus Status,
    IReadOnlyList<string> PermissionAccountIds,
    IReadOnlyList<SealAuthorizedUserRecord> AuthorizedUsers,
    bool AuthorizedUsersComplete);

/// <summary>
/// 定义 IFadadaTokenProvider 的稳定端口，使应用逻辑不依赖具体基础设施并便于替换测试实现。
/// </summary>
internal interface IFadadaTokenProvider
{
    ValueTask<string> GetAsync(DomainQueryContext context, CancellationToken cancellationToken);
}
