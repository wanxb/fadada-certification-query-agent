// Converts infrastructure failures to stable problem details without returning exception messages.
using Fadada.CertificationQueryAgent.Application.Persistence;
using Fadada.CertificationQueryAgent.Infrastructure.SqlServer2012;
using Microsoft.AspNetCore.Diagnostics;

namespace Fadada.CertificationQueryAgent.Web.Errors;

/// <summary>
/// 将未处理异常转换为稳定安全错误和追踪号，避免响应泄露内部细节。
/// </summary>
public sealed class SafeExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (httpContext.Response.HasStarted || exception is OperationCanceledException)
        {
            return false;
        }

        var (statusCode, errorCode) = exception switch
        {
            PersistenceConcurrencyException => (StatusCodes.Status409Conflict, "STORE_CONVERSATION_CONFLICT"),
            SqlPersistenceException => (StatusCodes.Status503ServiceUnavailable, "STORE_UNAVAILABLE"),
            InvalidOperationException { Message: var message } when message.StartsWith("CONFIG_", StringComparison.Ordinal) =>
                (StatusCodes.Status503ServiceUnavailable, "CONFIG_SERVICE_UNAVAILABLE"),
            _ => (StatusCodes.Status500InternalServerError, "SERVICE_REQUEST_FAILED")
        };
        httpContext.Response.Clear();
        httpContext.Response.StatusCode = statusCode;
        await httpContext.Response.WriteAsJsonAsync(
            new { errorCode, traceId = httpContext.TraceIdentifier },
            cancellationToken).ConfigureAwait(false);
        return true;
    }
}
