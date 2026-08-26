// Normalizes framework antiforgery exceptions into a non-sensitive JSON error contract.
using Microsoft.AspNetCore.Antiforgery;

namespace Fadada.CertificationQueryAgent.Web.Security;

/// <summary>
/// 把防伪验证异常转换为统一安全响应，同时保留服务端可关联日志。
/// </summary>
public sealed class AntiforgeryFailureMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context).ConfigureAwait(false);
        }
        catch (AntiforgeryValidationException) when (!context.Response.HasStarted)
        {
            context.Response.Clear();
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(
                new { errorCode = "AUTH_ANTIFORGERY_INVALID", traceId = context.TraceIdentifier },
                context.RequestAborted).ConfigureAwait(false);
        }
    }
}
