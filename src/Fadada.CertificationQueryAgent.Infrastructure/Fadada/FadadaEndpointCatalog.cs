// The frozen endpoint catalog prevents model input from selecting a URL or HTTP method.
using System.Collections.Frozen;

namespace Fadada.CertificationQueryAgent.Infrastructure.Fadada;

/// <summary>
/// 定义 FadadaEndpointKey 的受控状态集合，避免跨层使用未校验的自由文本。
/// </summary>
public enum FadadaEndpointKey
{
    AccessToken,
    GetAccount,
    GetPersonVerification,
    GetCompany,
    GetCompanyVerification,
    GetSeals,
    GetSealInfo
}

/// <summary>
/// 以不可变数据契约表达 FadadaEndpoint，确保跨层传递时字段语义和边界保持稳定。
/// </summary>
public sealed record FadadaEndpoint(
    FadadaEndpointKey Key,
    HttpMethod Method,
    string RelativePath,
    bool CredentialExchange = false);

/// <summary>
/// 维护法大大允许访问的固定端点目录，禁止模型或调用方构造任意 URL。
/// </summary>
public static class FadadaEndpointCatalog
{
    private static readonly FrozenDictionary<FadadaEndpointKey, FadadaEndpoint> Endpoints =
        new Dictionary<FadadaEndpointKey, FadadaEndpoint>
        {
            [FadadaEndpointKey.AccessToken] = new(FadadaEndpointKey.AccessToken, HttpMethod.Post, "/base/login/oauth2/accessToken", true),
            [FadadaEndpointKey.GetAccount] = Read(FadadaEndpointKey.GetAccount, "/user/api/account/getAccount"),
            [FadadaEndpointKey.GetPersonVerification] = Read(FadadaEndpointKey.GetPersonVerification, "/user/api/verify/person/result"),
            [FadadaEndpointKey.GetCompany] = Read(FadadaEndpointKey.GetCompany, "/user/api/company/getCompany"),
            [FadadaEndpointKey.GetCompanyVerification] = Read(FadadaEndpointKey.GetCompanyVerification, "/user/api/verify/company/result"),
            [FadadaEndpointKey.GetSeals] = Read(FadadaEndpointKey.GetSeals, "/base/api/seal/get"),
            [FadadaEndpointKey.GetSealInfo] = Read(FadadaEndpointKey.GetSealInfo, "/base/api/seal/getSealInfo")
        }.ToFrozenDictionary();

    public static IReadOnlyCollection<FadadaEndpoint> All { get; } =
        Array.AsReadOnly(Endpoints.Values.ToArray());

    public static FadadaEndpoint Get(FadadaEndpointKey key) => Endpoints[key];

    private static FadadaEndpoint Read(FadadaEndpointKey key, string path) =>
        new(key, HttpMethod.Get, path);
}
