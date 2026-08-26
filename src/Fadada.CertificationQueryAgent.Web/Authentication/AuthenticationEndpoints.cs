// Exposes cookie sign-in/out endpoints with antiforgery validation and generic failure responses.
using System.Security.Claims;
using System.Text.Encodings.Web;
using Fadada.CertificationQueryAgent.Application.Authentication;
using Fadada.CertificationQueryAgent.Web.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using DomainAuthenticationService = Fadada.CertificationQueryAgent.Application.Authentication.IAuthenticationService;

namespace Fadada.CertificationQueryAgent.Web.Authentication;

/// <summary>
/// 注册登录和退出端点，并在 Cookie 签发前执行限流、CSRF 和账号校验。
/// </summary>
public static class AuthenticationEndpoints
{
    public static IEndpointRouteBuilder MapAuthenticationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/login", LoginPage).AllowAnonymous();
        endpoints.MapPost("/auth/login", LoginAsync)
            .AllowAnonymous()
            .WithMetadata(new RequireAntiforgeryTokenAttribute(true))
            .RequireRateLimiting("login");
        endpoints.MapPost("/auth/logout", (Delegate)LogoutAsync)
            .WithMetadata(new RequireAntiforgeryTokenAttribute(true));
        return endpoints;
    }

    private static IResult LoginPage(HttpContext context, IAntiforgery antiforgery)
    {
        var token = antiforgery.GetAndStoreTokens(context).RequestToken ?? string.Empty;
        var encoded = HtmlEncoder.Default.Encode(token);
        var hasError = string.Equals(context.Request.Query["status"], "failed", StringComparison.Ordinal);
        var errorMarkup = hasError
            ? "<p class=\"login-error\" role=\"alert\">登录失败，请检查账号、密码或账号状态。</p>"
            : "<p class=\"login-error\" role=\"alert\" hidden></p>";
        return Results.Content($"""
            <!doctype html>
            <html lang="zh-CN">
            <head>
                <meta charset="utf-8">
                <meta name="viewport" content="width=device-width,initial-scale=1">
                <title>登录 | 法大大认证信息查询</title>
                <link rel="stylesheet" href="/css/app.css">
            </head>
            <body class="login-page">
                <main class="login-shell">
                    <section class="login-panel" aria-labelledby="login-title">
                        <div class="product-mark" aria-hidden="true">法</div>
                        <p class="product-name">法大大认证信息查询</p>
                        <h1 id="login-title">登录内部账号</h1>
                        <p class="login-intro">使用管理员分配的本地账号查询法大大认证信息。</p>
                        {errorMarkup}
                        <form method="post" action="/auth/login" data-login-form>
                            <input type="hidden" name="__RequestVerificationToken" value="{encoded}">
                            <label for="userName">账号</label>
                            <input id="userName" name="userName" autocomplete="username" maxlength="100" required autofocus>
                            <label for="password">密码</label>
                            <input id="password" name="password" type="password" autocomplete="current-password" maxlength="256" required>
                            <button class="primary-command login-submit" type="submit">登录</button>
                        </form>
                        <p class="login-footnote">仅限授权的内部人员使用</p>
                    </section>
                </main>
                <script src="/js/login.js" defer></script>
            </body>
            </html>
            """, "text/html; charset=utf-8");
    }

    private static async Task<IResult> LoginAsync(
        HttpContext context,
        DomainAuthenticationService authenticationService,
        CancellationToken cancellationToken)
    {
        LoginRequest? request;
        var browserFormPost = context.Request.HasFormContentType &&
            !context.Request.GetTypedHeaders().Accept.Any(value =>
                string.Equals(value.MediaType.Value, "application/json", StringComparison.OrdinalIgnoreCase));
        if (context.Request.HasFormContentType)
        {
            var form = await context.Request.ReadFormAsync(cancellationToken).ConfigureAwait(false);
            request = new LoginRequest(form["userName"].ToString(), form["password"].ToString());
        }
        else
        {
            request = await context.Request.ReadFromJsonAsync<LoginRequest>(cancellationToken).ConfigureAwait(false);
        }

        if (request is null)
        {
            return browserFormPost
                ? Results.Redirect("/login?status=failed")
                : Results.Json(new { errorCode = "AUTH_REQUEST_INVALID" }, statusCode: StatusCodes.Status400BadRequest);
        }

        var result = await authenticationService.AuthenticateAsync(request, cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded || result.UserId is null || result.SecurityStamp is null)
        {
            return browserFormPost
                ? Results.Redirect("/login?status=failed")
                : Results.Json(
                    new { errorCode = result.ErrorCode ?? "AUTH_INVALID_CREDENTIALS" },
                    statusCode: StatusCodes.Status401Unauthorized);
        }

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, result.UserId.Value.Value.ToString("D")),
            new Claim(ClaimTypes.Name, request.UserName.Trim()),
            new Claim(WebSecurityRegistration.SecurityStampClaim, result.SecurityStamp)
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, WebSecurityRegistration.CookieScheme));
        await context.SignInAsync(
            WebSecurityRegistration.CookieScheme,
            principal,
            new AuthenticationProperties
            {
                AllowRefresh = true,
                IsPersistent = false,
                ExpiresUtc = DateTimeOffset.UtcNow.AddHours(8)
            }).ConfigureAwait(false);
        return browserFormPost
            ? Results.Redirect("/")
            : Results.Ok(new { userName = request.UserName.Trim() });
    }

    private static async Task<IResult> LogoutAsync(HttpContext context)
    {
        await context.SignOutAsync(WebSecurityRegistration.CookieScheme).ConfigureAwait(false);
        return Results.NoContent();
    }
}
