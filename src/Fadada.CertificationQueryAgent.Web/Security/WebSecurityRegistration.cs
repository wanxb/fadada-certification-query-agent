// Configures local cookie identity, persistent data protection, and IP-based login throttling.
using System.Security.Claims;
using System.Threading.RateLimiting;
using Fadada.CertificationQueryAgent.Application.Authentication;
using Fadada.CertificationQueryAgent.Application.Common;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.RateLimiting;
using DomainAuthenticationService = Fadada.CertificationQueryAgent.Application.Authentication.IAuthenticationService;

namespace Fadada.CertificationQueryAgent.Web.Security;

/// <summary>
/// 集中注册 Cookie、Data Protection、防伪和授权策略，保持生产安全配置一致。
/// </summary>
public static class WebSecurityRegistration
{
    public const string CookieScheme = "FadadaCertificationQueryAgentCookie";
    public const string SecurityStampClaim = "fadada:certification_query_agent:security_stamp";

    public static IServiceCollection AddDomainAgentWebSecurity(
        this IServiceCollection services,
        IConfiguration configuration,
        IWebHostEnvironment environment)
    {
        var uiDemo = environment.IsDevelopment() &&
            string.Equals(configuration["Persistence:Profile"], "UiDemo", StringComparison.Ordinal);
        var keyPath = configuration["Security:DataProtectionKeysPath"] ??
            Path.Combine(environment.ContentRootPath, "App_Data", "DataProtection-Keys");
        var dataProtection = services
            .AddDataProtection()
            .SetApplicationName("Fadada.CertificationQueryAgent")
            .PersistKeysToFileSystem(new DirectoryInfo(Path.GetFullPath(keyPath)));
        if (OperatingSystem.IsWindows())
        {
            dataProtection.ProtectKeysWithDpapi(protectToLocalMachine: true);
        }

        services
            .AddAuthentication(CookieScheme)
            .AddCookie(CookieScheme, options =>
            {
                options.Cookie.Name = uiDemo ? "FadadaCertificationQueryAgentUiDemo" : "__Host-FadadaCertificationQueryAgent";
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Strict;
                options.Cookie.SecurePolicy = uiDemo ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
                options.Cookie.Path = "/";
                options.ExpireTimeSpan = TimeSpan.FromHours(8);
                options.SlidingExpiration = true;
                options.LoginPath = "/login";
                options.AccessDeniedPath = "/login";
                options.Events.OnRedirectToLogin = ApiStatusCode(CookieAuthenticationDefaults.ReturnUrlParameter, StatusCodes.Status401Unauthorized);
                options.Events.OnRedirectToAccessDenied = ApiStatusCode(CookieAuthenticationDefaults.ReturnUrlParameter, StatusCodes.Status403Forbidden);
                options.Events.OnValidatePrincipal = ValidatePrincipalAsync;
            });

        services.AddAuthorizationBuilder().SetFallbackPolicy(
            new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .Build());
        services.AddAntiforgery(options =>
        {
            options.Cookie.Name = uiDemo ? "FadadaCertificationQueryAgentUiDemoCsrf" : "__Host-FadadaCertificationQueryAgent-Csrf";
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.SecurePolicy = uiDemo ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
            options.HeaderName = "X-CSRF-TOKEN";
        });
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy("login", context => FixedWindow(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                configuration.GetValue("RateLimits:LoginPerMinute", 5)));
            options.AddPolicy("turn", context => FixedWindow(
                context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "anonymous",
                configuration.GetValue("RateLimits:TurnsPerMinute", 10)));
        });
        return services;
    }

    public static bool TryGetUserId(ClaimsPrincipal principal, out UserId userId)
    {
        if (Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var value) && value != Guid.Empty)
        {
            userId = new UserId(value);
            return true;
        }

        userId = default;
        return false;
    }

    private static RateLimitPartition<string> FixedWindow(string key, int permitLimit) =>
        RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            AutoReplenishment = true,
            PermitLimit = Math.Clamp(permitLimit, 1, 1000),
            QueueLimit = 0,
            Window = TimeSpan.FromMinutes(1)
        });

    private static Func<RedirectContext<CookieAuthenticationOptions>, Task> ApiStatusCode(
        string returnUrlParameter,
        int statusCode) => context =>
    {
        _ = returnUrlParameter;
        if (context.Request.Path.StartsWithSegments("/api") || context.Request.Path.StartsWithSegments("/auth"))
        {
            context.Response.StatusCode = statusCode;
            return Task.CompletedTask;
        }

        context.Response.Redirect(context.RedirectUri);
        return Task.CompletedTask;
    };

    private static async Task ValidatePrincipalAsync(CookieValidatePrincipalContext context)
    {
        var stamp = context.Principal?.FindFirstValue(SecurityStampClaim);
        if (context.Principal is null || !TryGetUserId(context.Principal, out var userId) || string.IsNullOrEmpty(stamp))
        {
            await RejectAsync(context).ConfigureAwait(false);
            return;
        }

        var authentication = context.HttpContext.RequestServices.GetRequiredService<DomainAuthenticationService>();
        if (!await authentication.ValidateSessionAsync(userId, stamp, context.HttpContext.RequestAborted).ConfigureAwait(false))
        {
            await RejectAsync(context).ConfigureAwait(false);
        }
    }

    private static async Task RejectAsync(CookieValidatePrincipalContext context)
    {
        context.RejectPrincipal();
        await context.HttpContext.SignOutAsync(CookieScheme).ConfigureAwait(false);
    }
}
