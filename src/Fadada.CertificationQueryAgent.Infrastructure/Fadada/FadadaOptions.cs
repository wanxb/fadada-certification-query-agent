// Validated provider options fail startup on insecure base URLs or unsafe retry and timeout values.
namespace Fadada.CertificationQueryAgent.Infrastructure.Fadada;

/// <summary>
/// 定义法大大连接与凭据配置，并在启动时强制 HTTPS 和合理超时。
/// </summary>
public sealed class FadadaOptions(
    Uri baseUri,
    string appId,
    string appSecret,
    TimeSpan requestTimeout,
    TimeSpan tokenRefreshSkew,
    int maximumGetRetries)
{
    public Uri BaseUri { get; } = baseUri;
    public string AppId { get; } = appId;
    public string AppSecret { get; } = appSecret;
    public TimeSpan RequestTimeout { get; } = requestTimeout;
    public TimeSpan TokenRefreshSkew { get; } = tokenRefreshSkew;
    public int MaximumGetRetries { get; } = maximumGetRetries;

    public void Validate()
    {
        if (!BaseUri.IsAbsoluteUri || BaseUri.Scheme != Uri.UriSchemeHttps ||
            string.IsNullOrWhiteSpace(AppId) || string.IsNullOrWhiteSpace(AppSecret) ||
            RequestTimeout <= TimeSpan.Zero || TokenRefreshSkew < TimeSpan.Zero ||
            MaximumGetRetries is < 0 or > 1)
        {
            throw new InvalidOperationException("Fadada configuration is invalid.");
        }
    }

    public override string ToString() =>
        $"FadadaOptions {{ BaseUri = {BaseUri}, AppId = [REDACTED], AppSecret = [REDACTED], RequestTimeout = {RequestTimeout}, TokenRefreshSkew = {TokenRefreshSkew}, MaximumGetRetries = {MaximumGetRetries} }}";
}

/// <summary>
/// 创建禁止自动重定向的 HTTP 处理器，防止凭据被转发到非预期主机。
/// </summary>
public static class FadadaHttpHandlerFactory
{
    public static HttpMessageHandler Create() => new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false
    };
}
