// Verifies SQL client isolation, parameterization, and the lab/production profile boundary.
using System.Text.RegularExpressions;

namespace Fadada.CertificationQueryAgent.ArchitectureTests;

/// <summary>
/// 验证 SqlPersistenceArchitectureTests 所覆盖的行为、边界条件和安全回归约束。
/// </summary>
public sealed partial class SqlPersistenceArchitectureTests
{
    [Fact]
    public void Sql_client_dependency_is_isolated_to_the_legacy_adapter()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "src");
        var violations = Directory
            .EnumerateFiles(sourceRoot, "*.*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".cs", StringComparison.Ordinal) || path.EndsWith(".csproj", StringComparison.Ordinal))
            .Where(path => !path.Contains(
                $"{Path.DirectorySeparatorChar}Fadada.CertificationQueryAgent.Infrastructure.SqlServer2012{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal))
            .Where(path => File.ReadAllText(path).Contains("Microsoft.Data.SqlClient", StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(repositoryRoot, path))
            .ToArray();

        Assert.Empty(violations);
    }

    [Fact]
    public void Repository_command_text_is_static_and_values_are_parameterized()
    {
        var adapterRoot = AdapterRoot();
        var source = string.Join('\n', Directory.EnumerateFiles(adapterRoot, "*.cs").Select(File.ReadAllText));

        Assert.DoesNotMatch(InterpolatedCommandText(), source);
        Assert.DoesNotMatch(AppendedCommandText(), source);
        Assert.Contains("SqlParameters.Add(command.Parameters", source, StringComparison.Ordinal);
        Assert.DoesNotContain("AddWithValue", source, StringComparison.Ordinal);
    }

    [Fact]
    public void User_owned_resource_queries_require_resource_and_user_parameters()
    {
        var adapterRoot = AdapterRoot();
        AssertContainsAll(
            Path.Combine(adapterRoot, "SqlServerConversationStore.cs"),
            "WHERE m.ConversationId = @conversationId AND c.UserId = @userId",
            "WHERE Id = @conversationId AND UserId = @userId");
        AssertContainsAll(
            Path.Combine(adapterRoot, "SqlServerStateStores.cs"),
            "WHERE s.ConversationId = @conversationId AND c.UserId = @userId",
            "WHERE Id = @id AND UserId = @userId");
        AssertContainsAll(
            Path.Combine(adapterRoot, "SqlServerConversationOwnershipVerifier.cs"),
            "WHERE Id = @conversationId AND UserId = @userId");
        AssertContainsAll(
            Path.Combine(adapterRoot, "SqlServerAuditStore.cs"),
            "t.ConversationId = @conversationId",
            "c.UserId = @userId");
    }

    [Fact]
    public void Retention_cleanup_is_bounded_and_targets_only_v2_content_tables()
    {
        var source = File.ReadAllText(Path.Combine(AdapterRoot(), "SqlServerDataLifecycleStore.cs"));

        Assert.Contains("TOP (@remaining)", source, StringComparison.Ordinal);
        Assert.Contains("FddAgentDiagnosticPayload", source, StringComparison.Ordinal);
        Assert.Contains("FddAgentSessionState", source, StringComparison.Ordinal);
        Assert.Contains("FddAgentMessage", source, StringComparison.Ordinal);
        Assert.Contains("FddAgentMaintenanceRun", source, StringComparison.Ordinal);
        Assert.DoesNotContain("FddAi" + "QueryService", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PSP", source, StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertContainsAll(string path, params string[] expectedFragments)
    {
        var source = File.ReadAllText(path);
        Assert.All(expectedFragments, fragment => Assert.Contains(fragment, source, StringComparison.Ordinal));
    }

    private static string AdapterRoot() =>
        Path.Combine(FindRepositoryRoot(), "src", "Fadada.CertificationQueryAgent.Infrastructure.SqlServer2012");

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

    [GeneratedRegex(@"CommandText\s*=\s*\$", RegexOptions.CultureInvariant)]
    private static partial Regex InterpolatedCommandText();

    [GeneratedRegex(@"CommandText\s*\+=", RegexOptions.CultureInvariant)]
    private static partial Regex AppendedCommandText();
}
