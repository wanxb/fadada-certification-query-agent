// JSON transport models are internal so external schema drift cannot become an application contract.
using System.Text.Json.Serialization;

namespace Fadada.CertificationQueryAgent.Infrastructure.Fadada;

/// <summary>
/// 承载 AccessTokenRequest 的已验证输入，限制跨层调用只能使用明确的数据契约。
/// </summary>
internal sealed record AccessTokenRequest(
    [property: JsonPropertyName("appId")] string AppId,
    [property: JsonPropertyName("timestamp")] string Timestamp,
    [property: JsonPropertyName("sign")] string Signature);

/// <summary>
/// 表示 AccessTokenPayload 的基础设施传输形状，仅用于适配外部数据且不作为应用层契约。
/// </summary>
internal sealed record AccessTokenPayload(
    [property: JsonPropertyName("accessToken")] string? AccessToken,
    [property: JsonPropertyName("expiresIn")] int ExpiresIn);

/// <summary>
/// 表示 FadadaEnvelope 的基础设施传输形状，仅用于适配外部数据且不作为应用层契约。
/// </summary>
internal sealed record FadadaEnvelope<T>(
    [property: JsonPropertyName("code")] string? Code,
    [property: JsonPropertyName("msg")] string? Message,
    [property: JsonPropertyName("data")] T? Data);

/// <summary>
/// 表示 AccountDto 的基础设施传输形状，仅用于适配外部数据且不作为应用层契约。
/// </summary>
internal sealed record AccountDto(string? AccountId, string? Mobile, string? Status);

/// <summary>
/// 表示 PersonVerificationDto 的基础设施传输形状，仅用于适配外部数据且不作为应用层契约。
/// </summary>
internal sealed record PersonVerificationDto(string? AccountId, string? Name, string? Status);

/// <summary>
/// 表示 AdministratorDto 的基础设施传输形状，仅用于适配外部数据且不作为应用层契约。
/// </summary>
internal sealed record AdministratorDto(string? AccountId, string? Name, string? Mobile);

/// <summary>
/// 表示 CompanyDto 的基础设施传输形状，仅用于适配外部数据且不作为应用层契约。
/// </summary>
internal sealed record CompanyDto(
    string? CompanyId,
    string? CompanyFullName,
    string? Status,
    AdministratorDto? Administrator);

/// <summary>
/// 表示 SealDto 的基础设施传输形状，仅用于适配外部数据且不作为应用层契约。
/// </summary>
internal sealed record SealDto(string? SealId, string? SealName, string? SealType, string? Status);

/// <summary>
/// 表示 SealInfoDto 的基础设施传输形状，仅用于适配外部数据且不作为应用层契约。
/// </summary>
internal sealed record SealInfoDto(
    string? SealId,
    string? SealName,
    string? SealType,
    string? Status,
    IReadOnlyList<string>? PermissionAccountIds);
