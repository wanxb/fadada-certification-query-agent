// Enforces repository-wide safety properties that ordinary unit tests cannot observe.
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Fadada.CertificationQueryAgent.ArchitectureTests;

/// <summary>
/// 验证 RepositorySafetyArchitectureTests 所覆盖的行为、边界条件和安全回归约束。
/// </summary>
public sealed partial class RepositorySafetyArchitectureTests
{
    private static readonly HashSet<string> CodeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".css", ".js", ".ps1", ".razor", ".sql"
    };

    private static readonly HashSet<string> ScannedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".csproj", ".css", ".gitignore", ".html", ".js", ".json", ".md", ".props",
        ".ps1", ".razor", ".sql", ".slnx", ".targets", ".xml", ".yaml", ".yml"
    };

    [Fact]
    public void Repository_contains_no_high_confidence_committed_secrets()
    {
        var findings = TextFiles()
            .SelectMany(path => File.ReadLines(path).Select((line, index) => new { path, line, number = index + 1 }))
            .Where(value => HighConfidenceSecret().IsMatch(value.line))
            .Select(value => $"{Path.GetRelativePath(RepositoryRoot(), value.path)}:{value.number}")
            .ToArray();

        Assert.True(findings.Length == 0, $"Potential secrets found at: {string.Join(", ", findings)}");
    }

    [Fact]
    public void Default_solution_tests_are_offline_and_live_SQL_is_explicitly_gated()
    {
        var root = RepositoryRoot();
        var solution = XDocument.Load(Path.Combine(root, "FadadaCertificationQueryAgent.slnx"));
        var projects = solution.Descendants("Project")
            .Select(value => value.Attribute("Path")?.Value ?? string.Empty)
            .ToArray();
        var testProjects = projects.Where(path => path.StartsWith("tests/", StringComparison.OrdinalIgnoreCase)).ToArray();

        Assert.Equal(6, testProjects.Length);
        Assert.DoesNotContain(testProjects, path => path.Contains("FddAi" + "QueryService", StringComparison.Ordinal));

        var integrationRoot = Path.Combine(root, "tests", "Fadada.CertificationQueryAgent.IntegrationTests");
        var sqlTests = File.ReadAllText(Path.Combine(integrationRoot, "SqlServer2012PersistenceTests.cs"));
        Assert.Contains("FDD_RUN_SQL2012_READINESS_TESTS", sqlTests, StringComparison.Ordinal);
        Assert.Contains("FDD_RUN_SQL2012_REPOSITORY_TESTS", sqlTests, StringComparison.Ordinal);
        Assert.Equal(2, Count(sqlTests, "Environment.GetEnvironmentVariable(\"FDD_RUN_SQL2012_"));

        var externalUrls = testProjects
            .SelectMany(project => TextFiles(Path.Combine(root, Path.GetDirectoryName(project)!)))
            .SelectMany(path => UrlPattern().Matches(File.ReadAllText(path)).Select(match => new
            {
                Path = Path.GetRelativePath(root, path),
                Url = match.Value
            }))
            .Where(value => !IsOfflineTestUrl(value.Url))
            .Select(value => $"{value.Path}: {value.Url}")
            .ToArray();
        Assert.True(externalUrls.Length == 0, $"Ungated test URLs found: {string.Join(", ", externalUrls)}");
    }

    [Fact]
    public void Local_settings_are_ignored_by_scans_and_forbidden_from_publish()
    {
        var root = RepositoryRoot();
        var program = File.ReadAllText(Path.Combine(root, "src", "Fadada.CertificationQueryAgent.Web", "Program.cs"));
        var project = File.ReadAllText(Path.Combine(root, "src", "Fadada.CertificationQueryAgent.Web", "Fadada.CertificationQueryAgent.Web.csproj"));
        var gitignore = File.ReadAllText(Path.Combine(root, ".gitignore"));

        Assert.Contains("AddJsonFile(\"appsettings.Local.json\"", program, StringComparison.Ordinal);
        Assert.Contains("appsettings.Local.json", gitignore, StringComparison.Ordinal);
        Assert.Contains("<Content Update=\"appsettings.Local.json\">", project, StringComparison.Ordinal);
        Assert.Contains("<CopyToOutputDirectory>Never</CopyToOutputDirectory>", project, StringComparison.Ordinal);
        Assert.Contains("<CopyToPublishDirectory>Never</CopyToPublishDirectory>", project, StringComparison.Ordinal);
    }

    [Fact]
    public void Repository_contains_only_the_current_product_structure_and_brand()
    {
        var root = RepositoryRoot();
        Assert.False(Directory.Exists(Path.Combine(root, "spikes")));
        Assert.True(File.Exists(Path.Combine(root, "FadadaCertificationQueryAgent.slnx")));
        Assert.False(File.Exists(Path.Combine(root, "Fdd" + "DomainAgent.slnx")));

        var solution = XDocument.Load(Path.Combine(root, "FadadaCertificationQueryAgent.slnx"));
        var projectPaths = solution.Descendants("Project")
            .Select(value => value.Attribute("Path")?.Value ?? string.Empty)
            .ToArray();
        Assert.Equal(13, projectPaths.Length);
        Assert.All(projectPaths, path => Assert.Contains("Fadada.CertificationQueryAgent", path, StringComparison.Ordinal));
        Assert.DoesNotContain(projectPaths, path => path.StartsWith("spikes/", StringComparison.OrdinalIgnoreCase));

        var forbidden = new[]
        {
            "Fdd." + "DomainAgent",
            "FddAi" + "QueryService",
            "Control" + "V1Target",
            "Treatment" + "AgentTarget",
            "control-" + "golden.v1.json",
            "法务" + "查询工作台"
        };
        var findings = TextFiles()
            .Select(path => new { path, content = File.ReadAllText(path) })
            .SelectMany(file => forbidden
                .Where(value => file.content.Contains(value, StringComparison.OrdinalIgnoreCase))
                .Select(value => $"{Path.GetRelativePath(root, file.path)}: {value}"))
            .ToArray();
        Assert.Empty(findings);

        var login = File.ReadAllText(Path.Combine(
            root, "src", "Fadada.CertificationQueryAgent.Web", "Authentication", "AuthenticationEndpoints.cs"));
        var workbench = File.ReadAllText(Path.Combine(
            root, "src", "Fadada.CertificationQueryAgent.Web", "Components", "Pages", "Workbench.razor"));
        Assert.Contains("法大大认证信息查询", login, StringComparison.Ordinal);
        Assert.Contains("法大大认证信息查询", workbench, StringComparison.Ordinal);
    }

    [Fact]
    public void Production_packages_exclude_the_unused_OpenAI_agent_adapter()
    {
        var productionProjects = Directory.EnumerateFiles(
            Path.Combine(RepositoryRoot(), "src"), "*.csproj", SearchOption.AllDirectories);
        var forbiddenPackage = "Microsoft.Agents.AI." + "OpenAI";

        Assert.DoesNotContain(productionProjects, path =>
            File.ReadAllText(path).Contains(forbiddenPackage, StringComparison.OrdinalIgnoreCase));
        var agentHost = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src", "Fadada.CertificationQueryAgent.AgentHost",
            "Fadada.CertificationQueryAgent.AgentHost.csproj"));
        Assert.Contains("Microsoft.Agents.AI", agentHost, StringComparison.Ordinal);
        Assert.Contains("Microsoft.Extensions.AI", agentHost, StringComparison.Ordinal);
    }

    [Fact]
    public void Storage_compatibility_identifiers_do_not_escape_the_adapter_boundary()
    {
        var root = RepositoryRoot();
        var sourceRoot = Path.Combine(root, "src");
        var adapterSegment = $"{Path.DirectorySeparatorChar}Fadada.CertificationQueryAgent.Infrastructure.SqlServer2012{Path.DirectorySeparatorChar}";
        var connectionKeyFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine(sourceRoot, "Fadada.CertificationQueryAgent.Web", "Configuration", "DomainAgentServiceRegistration.cs"),
            Path.Combine(sourceRoot, "Fadada.CertificationQueryAgent.Web", "Health", "StoreReadinessHealthCheck.cs")
        };
        var violations = TextFiles(sourceRoot)
            .Select(path => new { path, content = File.ReadAllText(path) })
            .Where(file => file.content.Contains("FddAgent", StringComparison.Ordinal) ||
                file.content.Contains("FadadaAgentLab", StringComparison.Ordinal) ||
                file.content.Contains("FddDomainAgent", StringComparison.Ordinal))
            .Where(file => !file.path.Contains(adapterSegment, StringComparison.OrdinalIgnoreCase))
            .Where(file => !(connectionKeyFiles.Contains(file.path) &&
                !file.content.Contains("FddAgent", StringComparison.Ordinal) &&
                !file.content.Contains("FadadaAgentLab", StringComparison.Ordinal)))
            .Select(file => Path.GetRelativePath(root, file.path))
            .ToArray();

        Assert.Empty(violations);
    }

    [Fact]
    public void Maintained_code_files_contain_explanatory_comments()
    {
        var root = RepositoryRoot();
        var maintainedRoots = new[] { "src", "tools", "tests", "scripts", "database" }
            .Select(directory => Path.Combine(root, directory));
        var missingComments = maintainedRoots
            .SelectMany(directory => Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            .Where(path => CodeExtensions.Contains(Path.GetExtension(path)))
            .Where(path => !IsGeneratedOrPrivate(path))
            .Where(path => !ExplanatoryComment().IsMatch(File.ReadAllText(path)))
            .Select(path => Path.GetRelativePath(root, path))
            .ToArray();

        // Comments must explain a responsibility, constraint, or non-obvious decision. The gate
        // detects missing documentation, while review remains responsible for comment quality.
        Assert.True(
            missingComments.Length == 0,
            $"Code files without explanatory comments: {string.Join(", ", missingComments)}");
    }

    [Fact]
    public void Maintained_CSharp_types_have_adjacent_Chinese_XML_documentation()
    {
        var root = RepositoryRoot();
        var findings = new[] { "src", "tools", "tests" }
            .Select(directory => Path.Combine(root, directory))
            .SelectMany(directory => Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories))
            .Where(path => !IsGeneratedOrPrivate(path))
            .SelectMany(UndocumentedTypes)
            .ToArray();

        // 类型注释必须紧邻声明，门禁才能明确判断注释属于哪个类型，而不是误用文件级说明。
        Assert.True(
            findings.Length == 0,
            $"C# types without adjacent Chinese XML documentation: {string.Join(", ", findings)}");
    }

    private static bool IsOfflineTestUrl(string value)
    {
        var uri = new Uri(value);
        return uri.IsLoopback ||
            uri.Host.EndsWith(".example", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.EndsWith(".invalid", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.EndsWith(".test", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(uri.Host, "collector.internal", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> TextFiles(string? root = null) =>
        Directory.EnumerateFiles(root ?? RepositoryRoot(), "*", SearchOption.AllDirectories)
            .Where(path => ScannedExtensions.Contains(Path.GetExtension(path)))
            .Where(path => !IsGeneratedOrPrivate(path));

    private static bool IsGeneratedOrPrivate(string path) =>
        IsLocalSettings(Path.GetFileName(path)) ||
        path.Split(Path.DirectorySeparatorChar).Any(segment =>
            segment is ".git" or "bin" or "obj" or "TestResults" or "artifacts" or ".vs");

    private static bool IsLocalSettings(string fileName) =>
        string.Equals(fileName, "appsettings.Local.json", StringComparison.OrdinalIgnoreCase) ||
        (fileName.StartsWith("appsettings.", StringComparison.OrdinalIgnoreCase) &&
            fileName.EndsWith(".Local.json", StringComparison.OrdinalIgnoreCase));

    private static int Count(string value, string needle) =>
        value.Split(needle, StringSplitOptions.None).Length - 1;

    private static IEnumerable<string> UndocumentedTypes(string path)
    {
        var lines = File.ReadAllLines(path);
        for (var index = 0; index < lines.Length; index++)
        {
            var declaration = TypeDeclaration().Match(lines[index]);
            if (!declaration.Success)
            {
                continue;
            }

            var documentation = new List<string>();
            for (var cursor = index - 1;
                 cursor >= 0 && lines[cursor].TrimStart().StartsWith("///", StringComparison.Ordinal);
                 cursor--)
            {
                documentation.Add(lines[cursor]);
            }

            var combined = string.Join('\n', documentation);
            if (!combined.Contains("<summary>", StringComparison.Ordinal) ||
                !combined.Contains("</summary>", StringComparison.Ordinal) ||
                !ChineseText().IsMatch(combined))
            {
                yield return $"{Path.GetRelativePath(RepositoryRoot(), path)}:{index + 1} ({declaration.Groups["name"].Value})";
            }
        }
    }

    private static string RepositoryRoot()
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

    [GeneratedRegex("(?i)(sk-[a-z0-9_-]{20,}|gh[pousr]_[a-z0-9]{20,}|-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----|AccountKey\\s*=\\s*[^;\\s]{16,}|Authorization\\s*[:=]\\s*[\\\"']?Bearer\\s+[a-z0-9._-]{20,})", RegexOptions.CultureInvariant)]
    private static partial Regex HighConfidenceSecret();

    [GeneratedRegex(@"https?://[A-Za-z0-9._:-]+", RegexOptions.CultureInvariant)]
    private static partial Regex UrlPattern();

    [GeneratedRegex(@"(?m)^\s*(?://|/\*|\*|@\*|#|--)", RegexOptions.CultureInvariant)]
    private static partial Regex ExplanatoryComment();

    [GeneratedRegex(@"^\s*(?:(?:public|internal|private|protected|file)\s+)?(?:(?:sealed|static|abstract|partial|readonly)\s+)*(?:class|record(?:\s+struct)?|interface|enum|struct)\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)", RegexOptions.CultureInvariant)]
    private static partial Regex TypeDeclaration();

    [GeneratedRegex("[\\u4e00-\\u9fff]", RegexOptions.CultureInvariant)]
    private static partial Regex ChineseText();
}
