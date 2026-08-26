// Locks project references to the intended clean-architecture dependency direction.
using System.Xml.Linq;
using Fadada.CertificationQueryAgent.AgentHost.Middleware;
using Fadada.CertificationQueryAgent.Application.DomainTools;
using Fadada.CertificationQueryAgent.Infrastructure.Fadada;

namespace Fadada.CertificationQueryAgent.ArchitectureTests;

/// <summary>
/// 验证 ProjectDependencyTests 所覆盖的行为、边界条件和安全回归约束。
/// </summary>
public sealed class ProjectDependencyTests
{
    private static readonly IReadOnlyDictionary<string, string[]> AllowedReferences =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["Fadada.CertificationQueryAgent.Domain"] = [],
            ["Fadada.CertificationQueryAgent.Application"] = ["Fadada.CertificationQueryAgent.Domain"],
            ["Fadada.CertificationQueryAgent.AgentHost"] =
                ["Fadada.CertificationQueryAgent.Application", "Fadada.CertificationQueryAgent.Domain"],
            ["Fadada.CertificationQueryAgent.Infrastructure"] =
                ["Fadada.CertificationQueryAgent.Application", "Fadada.CertificationQueryAgent.Domain"],
            ["Fadada.CertificationQueryAgent.Infrastructure.SqlServer2012"] =
                ["Fadada.CertificationQueryAgent.Application", "Fadada.CertificationQueryAgent.Domain"],
            ["Fadada.CertificationQueryAgent.Web"] =
            [
                "Fadada.CertificationQueryAgent.AgentHost",
                "Fadada.CertificationQueryAgent.Application",
                "Fadada.CertificationQueryAgent.Infrastructure",
                "Fadada.CertificationQueryAgent.Infrastructure.SqlServer2012",
            ],
            ["Fadada.CertificationQueryAgent.Admin"] =
            [
                "Fadada.CertificationQueryAgent.Application",
                "Fadada.CertificationQueryAgent.Infrastructure",
                "Fadada.CertificationQueryAgent.Infrastructure.SqlServer2012",
            ],
        };

    [Fact]
    public void Production_projects_have_only_approved_direct_dependencies()
    {
        var repositoryRoot = FindRepositoryRoot();

        foreach (var (projectName, expectedReferences) in AllowedReferences)
        {
            var projectPath = FindProject(repositoryRoot, projectName);
            var document = XDocument.Load(projectPath);
            var actualReferences = document
                .Descendants("ProjectReference")
                .Select(reference => reference.Attribute("Include")?.Value)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => Path.GetFileNameWithoutExtension(path!))
                .Order(StringComparer.Ordinal)
                .ToArray();

            Assert.Equal(
                expectedReferences.Order(StringComparer.Ordinal),
                actualReferences);
        }
    }

    [Fact]
    public void Fadada_endpoint_catalog_is_the_exact_approved_set()
    {
        var actual = FadadaEndpointCatalog.All
            .Select(endpoint => $"{endpoint.Key}|{endpoint.Method.Method}|{endpoint.RelativePath}|{endpoint.CredentialExchange}")
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            new[]
            {
                "AccessToken|POST|/base/login/oauth2/accessToken|True",
                "GetAccount|GET|/user/api/account/getAccount|False",
                "GetCompanyVerification|GET|/user/api/verify/company/result|False",
                "GetCompany|GET|/user/api/company/getCompany|False",
                "GetPersonVerification|GET|/user/api/verify/person/result|False",
                "GetSealInfo|GET|/base/api/seal/getSealInfo|False",
                "GetSeals|GET|/base/api/seal/get|False"
            }.Order(StringComparer.Ordinal),
            actual);
        var mutableView = Assert.IsAssignableFrom<ICollection<FadadaEndpoint>>(FadadaEndpointCatalog.All);
        Assert.Throws<NotSupportedException>(() => mutableView.Clear());
    }

    [Fact]
    public void Tool_policy_order_and_registered_tools_are_exact()
    {
        Assert.Equal(
            new[]
            {
                "authenticated-principal",
                "conversation-ownership",
                "registered-tool",
                "tool-schema",
                "argument-provenance",
                "turn-budget",
                "tool-audit-gate",
                "tool-execution",
                "tool-result-sanitization",
                "post-response-evidence"
            },
            ToolPolicyPipeline.OrderedPolicyIds);
        Assert.Equal(
            new[] { "query_company", "query_person", "query_relationship", "query_seals" },
            DomainToolRegistry.All.Select(tool => tool.Name));
        var mutableView = Assert.IsAssignableFrom<ICollection<DomainToolDefinition>>(DomainToolRegistry.All);
        Assert.Throws<NotSupportedException>(() => mutableView.Clear());
    }

    [Fact]
    public void AgentHost_contains_no_out_of_scope_agent_architectures()
    {
        var repositoryRoot = FindRepositoryRoot();
        var agentHostRoot = Path.Combine(repositoryRoot, "src", "Fadada.CertificationQueryAgent.AgentHost");
        var source = string.Join(
            '\n',
            Directory.EnumerateFiles(agentHostRoot, "*.cs", SearchOption.AllDirectories)
                .Select(File.ReadAllText));

        Assert.DoesNotContain("Workflow", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Mcp", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Rag", source, StringComparison.Ordinal);
        Assert.DoesNotContain("CodeInterpreter", source, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(source, "new ChatClientAgent("));
    }

    private static int CountOccurrences(string value, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = value.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    private static string FindProject(string repositoryRoot, string projectName)
    {
        var matches = Directory
            .EnumerateFiles(repositoryRoot, $"{projectName}.csproj", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToArray();

        return Assert.Single(matches);
    }

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
