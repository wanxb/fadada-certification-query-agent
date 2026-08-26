// Rejects endpoint-filter antiforgery failures before a state-changing handler can run.
using Microsoft.AspNetCore.Antiforgery;

namespace Fadada.CertificationQueryAgent.Web.Security;

/// <summary>
/// 对所有状态变更请求强制校验防伪令牌，保护 Cookie 登录会话免受 CSRF。
/// </summary>
public sealed class AntiforgeryEnforcementMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var validation = context.Features.Get<IAntiforgeryValidationFeature>();
        if (validation is { IsValid: false })
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(
                new { errorCode = "AUTH_ANTIFORGERY_INVALID", traceId = context.TraceIdentifier },
                context.RequestAborted).ConfigureAwait(false);
            return;
        }

        await next(context).ConfigureAwait(false);
    }
}
