// Composition root: selects an explicit runtime profile and wires security before application endpoints.
using System.Net;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Fadada.CertificationQueryAgent.Infrastructure.Telemetry;
using Fadada.CertificationQueryAgent.Web.Authentication;
using Fadada.CertificationQueryAgent.Web.Components;
using Fadada.CertificationQueryAgent.Web.Configuration;
using Fadada.CertificationQueryAgent.Web.Conversations;
using Fadada.CertificationQueryAgent.Web.Errors;
using Fadada.CertificationQueryAgent.Web.Health;
using Fadada.CertificationQueryAgent.Web.Rendering;
using Fadada.CertificationQueryAgent.Web.Security;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);
builder.Services.AddCertificationQueryTelemetry(builder.Configuration, builder.Environment);
builder.Services.AddDomainAgentRuntime(builder.Configuration, builder.Environment);
builder.Services.AddDomainAgentWebSecurity(builder.Configuration, builder.Environment);
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddSingleton<AgentMarkdownRenderer>();
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Encoder = JavaScriptEncoder.Default;
    options.SerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
});
builder.Services.Configure<Microsoft.AspNetCore.Routing.RouteHandlerOptions>(options =>
    options.ThrowOnBadRequest = false);
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddExceptionHandler<SafeExceptionHandler>();
builder.Services.AddProblemDetails();
builder.Services
    .AddHealthChecks()
    .AddCheck<StoreReadinessHealthCheck>("store", tags: ["ready"]);
builder.Services.AddAuthorizationBuilder().AddPolicy("ReadyHealth", policy => policy.RequireAssertion(context =>
    context.Resource is HttpContext httpContext &&
    IPAddress.IsLoopback(httpContext.Connection.RemoteIpAddress ?? IPAddress.None)));

var app = builder.Build();
app.UseExceptionHandler();
app.UseMiddleware<SecurityHeadersMiddleware>();
app.UseStaticFiles();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<AntiforgeryFailureMiddleware>();
app.UseAntiforgery();
app.UseMiddleware<AntiforgeryEnforcementMiddleware>();

app.MapAuthenticationEndpoints();
app.MapConversationEndpoints();
app.MapStaticAssets().AllowAnonymous();
app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = _ => false,
    ResponseWriter = WriteSafeHealthResponseAsync
}).AllowAnonymous();
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = registration => registration.Tags.Contains("ready"),
    ResponseWriter = WriteSafeHealthResponseAsync
}).RequireAuthorization("ReadyHealth");
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

static Task WriteSafeHealthResponseAsync(HttpContext context, HealthReport report)
{
    context.Response.ContentType = "application/json; charset=utf-8";
    return JsonSerializer.SerializeAsync(
        context.Response.Body,
        new { status = report.Status.ToString() },
        cancellationToken: context.RequestAborted);
}

/// <summary>
/// 作为应用或评测入口集中完成依赖装配和启动，避免初始化顺序散落到业务代码。
/// </summary>
public partial class Program;
