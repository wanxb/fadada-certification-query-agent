// Verifies security middleware ordering and static restrictions that must survive refactoring.
using System.Text.RegularExpressions;

namespace Fadada.CertificationQueryAgent.ArchitectureTests;

/// <summary>
/// 验证 WebSecurityArchitectureTests 所覆盖的行为、边界条件和安全回归约束。
/// </summary>
public sealed class WebSecurityArchitectureTests
{
    [Fact]
    public void Authentication_and_cookie_controls_are_explicit()
    {
        var source = Read("src", "Fadada.CertificationQueryAgent.Web", "Security", "WebSecurityRegistration.cs");

        Assert.Contains("SetFallbackPolicy", source, StringComparison.Ordinal);
        Assert.Contains("RequireAuthenticatedUser", source, StringComparison.Ordinal);
        Assert.Contains("Cookie.HttpOnly = true", source, StringComparison.Ordinal);
        Assert.Contains("SameSiteMode.Strict", source, StringComparison.Ordinal);
        Assert.Contains("CookieSecurePolicy.Always", source, StringComparison.Ordinal);
        Assert.Contains("TimeSpan.FromHours(8)", source, StringComparison.Ordinal);
        Assert.Contains("PersistKeysToFileSystem", source, StringComparison.Ordinal);
        Assert.Contains("ProtectKeysWithDpapi", source, StringComparison.Ordinal);
        Assert.Contains("ValidateSessionAsync", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_state_changing_endpoint_has_antiforgery_and_required_rate_limits()
    {
        var authentication = Read("src", "Fadada.CertificationQueryAgent.Web", "Authentication", "AuthenticationEndpoints.cs");
        var conversations = Read("src", "Fadada.CertificationQueryAgent.Web", "Conversations", "ConversationEndpoints.cs");

        Assert.Equal(2, Count(authentication, "MapPost("));
        Assert.Equal(2, Count(authentication, "RequireAntiforgeryTokenAttribute(true)"));
        Assert.Contains("RequireRateLimiting(\"login\")", authentication, StringComparison.Ordinal);
        Assert.Equal(4, Count(conversations, "MapPost("));
        Assert.Equal(4, Count(conversations, "RequireAntiforgeryTokenAttribute(true)"));
        Assert.Contains("RequireRateLimiting(\"turn\")", conversations, StringComparison.Ordinal);
    }

    [Fact]
    public void Sse_transport_uses_an_explicit_event_allowlist()
    {
        var source = Read("src", "Fadada.CertificationQueryAgent.Web", "Conversations", "ConversationEndpoints.cs");

        Assert.Contains("event: {mapped.Item1}", source, StringComparison.Ordinal);
        Assert.Contains("SafeToolName", source, StringComparison.Ordinal);
        Assert.Contains("SafeCode", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Serialize(value", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ArgumentsJson", source, StringComparison.Ordinal);
        Assert.DoesNotContain("PromptSha256 =", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Web_exposes_only_the_approved_routes_and_Blazor_host_boundary()
    {
        var authentication = Read("src", "Fadada.CertificationQueryAgent.Web", "Authentication", "AuthenticationEndpoints.cs");
        var conversations = Read("src", "Fadada.CertificationQueryAgent.Web", "Conversations", "ConversationEndpoints.cs");
        var program = Read("src", "Fadada.CertificationQueryAgent.Web", "Program.cs");

        Assert.Equal(
            ["GET /login", "POST /auth/login", "POST /auth/logout"],
            LiteralRoutes(authentication));
        Assert.Equal(
            [
                "GET /api/v1/conversations/",
                "GET /api/v1/conversations/{id:guid}",
                "POST /api/v1/conversations/",
                "POST /api/v1/conversations/{id:guid}/archive",
                "POST /api/v1/conversations/{id:guid}/restore",
                "POST /api/v1/conversations/{id:guid}/turns"
            ],
            LiteralRoutes(conversations, "/api/v1/conversations"));
        Assert.Equal(2, Count(program, "MapHealthChecks("));
        Assert.Contains("MapHealthChecks(\"/health/live\"", program, StringComparison.Ordinal);
        Assert.Contains("MapHealthChecks(\"/health/ready\"", program, StringComparison.Ordinal);
        Assert.Equal(1, Count(program, "MapStaticAssets()"));
        Assert.Equal(1, Count(program, "MapRazorComponents<App>()"));
        Assert.DoesNotContain("MapPut(", program + authentication + conversations, StringComparison.Ordinal);
        Assert.DoesNotContain("MapDelete(", program + authentication + conversations, StringComparison.Ordinal);
        Assert.DoesNotContain("MapPatch(", program + authentication + conversations, StringComparison.Ordinal);
        Assert.DoesNotContain("MapMethods(", program + authentication + conversations, StringComparison.Ordinal);
    }

    [Fact]
    public void Web_UI_allows_only_centralized_sanitized_markup_and_no_dynamic_script_surface()
    {
        var webRoot = Path.Combine(FindRepositoryRoot(), "src", "Fadada.CertificationQueryAgent.Web");
        var files = Directory.EnumerateFiles(webRoot, "*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(".razor", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
            // 只约束项目维护的源文件；bin/obj 会包含框架和第三方静态资源，不能作为自有脚本接受门禁。
            .Where(path => !path.Split(Path.DirectorySeparatorChar).Any(segment => segment is "bin" or "obj"))
            .Select(path => new { Path = path, Content = File.ReadAllText(path) })
            .ToArray();
        var source = string.Join('\n', files.Select(file => file.Content));
        var markupFiles = files
            .Where(file => file.Content.Contains("MarkupString", StringComparison.Ordinal))
            .Select(file => Path.GetRelativePath(webRoot, file.Path))
            .ToArray();
        var renderer = File.ReadAllText(Path.Combine(webRoot, "Rendering", "AgentMarkdownRenderer.cs"));

        Assert.Equal([Path.Combine("Rendering", "AgentMarkdownRenderer.cs")], markupFiles);
        Assert.Contains(".DisableHtml()", renderer, StringComparison.Ordinal);
        Assert.Contains("sanitizer.Sanitize(html)", renderer, StringComparison.Ordinal);
        Assert.DoesNotContain("innerHTML", source, StringComparison.Ordinal);
        Assert.DoesNotContain("outerHTML", source, StringComparison.Ordinal);
        Assert.DoesNotContain("insertAdjacentHTML", source, StringComparison.Ordinal);
        Assert.DoesNotContain("document.write", source, StringComparison.Ordinal);
        Assert.DoesNotContain("eval(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("new Function(", source, StringComparison.Ordinal);
    }

    private static string[] LiteralRoutes(string source, string prefix = "") =>
        Regex.Matches(source, "(?:endpoints|group)\\.Map(?<method>Get|Post)\\(\\\"(?<path>[^\\\"]+)\\\"")
            .Select(match => $"{match.Groups["method"].Value.ToUpperInvariant()} {prefix}{match.Groups["path"].Value}")
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static int Count(string value, string needle) =>
        value.Split(needle, StringSplitOptions.None).Length - 1;

    private static string Read(params string[] path) =>
        File.ReadAllText(Path.Combine([FindRepositoryRoot(), .. path]));

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "FadadaCertificationQueryAgent.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
