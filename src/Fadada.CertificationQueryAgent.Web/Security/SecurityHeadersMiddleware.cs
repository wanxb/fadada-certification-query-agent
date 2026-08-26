// Applies restrictive browser policies and no-store semantics to every dynamic response.
namespace Fadada.CertificationQueryAgent.Web.Security;

/// <summary>
/// 为每个响应添加浏览器安全头，限制内容来源、嵌入和 MIME 嗅探。
/// </summary>
public sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        context.Response.OnStarting(() =>
        {
            var headers = context.Response.Headers;
            headers.ContentSecurityPolicy = "default-src 'self'; base-uri 'none'; frame-ancestors 'none'; form-action 'self'; object-src 'none'";
            headers.XContentTypeOptions = "nosniff";
            headers["Referrer-Policy"] = "no-referrer";
            headers.CacheControl = "no-store, max-age=0";
            headers.Pragma = "no-cache";
            headers.Append("Permissions-Policy", "camera=(), microphone=(), geolocation=()");
            return Task.CompletedTask;
        });
        await next(context).ConfigureAwait(false);
    }
}
