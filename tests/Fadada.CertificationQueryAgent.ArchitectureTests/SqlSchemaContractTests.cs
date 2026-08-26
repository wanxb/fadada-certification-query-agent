// Treats the reviewed SQL Server schema and indexes as a versioned persistence contract.
using System.Text.RegularExpressions;

namespace Fadada.CertificationQueryAgent.ArchitectureTests;

/// <summary>
/// 验证 SqlSchemaContractTests 所覆盖的行为、边界条件和安全回归约束。
/// </summary>
public sealed partial class SqlSchemaContractTests
{
    private static readonly string[] RequiredTables =
    [
        "FddAgentUser",
        "FddAgentSecurityEvent",
        "FddAgentConversation",
        "FddAgentMessage",
        "FddAgentTurn",
        "FddAgentModelCall",
        "FddAgentToolCall",
        "FddAgentExternalCall",
        "FddAgentSessionState",
        "FddAgentDiagnosticPayload",
        "FddAgentMaintenanceRun"
    ];

    [Fact]
    public void V2_schema_is_manual_idempotent_and_contains_the_required_table_set()
    {
        var directory = V2Directory();
        var schema = File.ReadAllText(Path.Combine(directory, "001-create-schema.sql"));
        var indexes = File.ReadAllText(Path.Combine(directory, "002-create-indexes.sql"));
        var boundedMultiTool = File.ReadAllText(Path.Combine(directory, "004-enable-bounded-multi-tool-turns.sql"));
        var readme = File.ReadAllText(Path.Combine(directory, "README.md"));

        foreach (var table in RequiredTables)
        {
            Assert.Contains($"CREATE TABLE dbo.{table}", schema, StringComparison.Ordinal);
            Assert.Contains($"OBJECT_ID(N'dbo.{table}', N'U') IS NULL", schema, StringComparison.Ordinal);
        }

        Assert.Contains("FddAgentSchemaVersion", schema, StringComparison.Ordinal);
        Assert.Contains("BEGIN TRANSACTION", schema, StringComparison.Ordinal);
        Assert.Contains("ROWVERSION", schema, StringComparison.Ordinal);
        Assert.Contains("DATETIME2(3)", schema, StringComparison.Ordinal);
        Assert.Contains("VARBINARY(MAX)", schema, StringComparison.Ordinal);
        Assert.Contains("IF NOT EXISTS", indexes, StringComparison.Ordinal);
        Assert.Contains("AttemptNumber BETWEEN 1 AND 4", schema, StringComparison.Ordinal);
        Assert.Contains("SequenceNumber BETWEEN 1 AND 3", schema, StringComparison.Ordinal);
        Assert.Contains("ModelCallCount <= 4", schema, StringComparison.Ordinal);
        Assert.Contains("ToolCallCount <= 3", schema, StringComparison.Ordinal);
        Assert.Contains("CK_FddAgentTurn_Counts", boundedMultiTool, StringComparison.Ordinal);
        Assert.Contains("CK_FddAgentModelCall_Attempt", boundedMultiTool, StringComparison.Ordinal);
        Assert.Contains("CK_FddAgentToolCall_Sequence", boundedMultiTool, StringComparison.Ordinal);
        Assert.Contains("SET SchemaVersion = 2", boundedMultiTool, StringComparison.Ordinal);
        Assert.Contains("ScriptId = N'004-enable-bounded-multi-tool-turns'", boundedMultiTool, StringComparison.Ordinal);
        Assert.Contains("manual operations", readme, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("never create or migrate schema during startup", readme, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void V2_scripts_do_not_contain_destructive_or_unsupported_sql()
    {
        var scripts = Directory
            .EnumerateFiles(V2Directory(), "*.sql", SearchOption.TopDirectoryOnly)
            .Select(File.ReadAllText)
            .ToArray();
        var all = string.Join('\n', scripts);

        Assert.DoesNotContain("DROP TABLE", all, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("TRUNCATE TABLE", all, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DELETE FROM", all, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CREATE OR ALTER", all, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("JSON_VALUE", all, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("FOR SYSTEM_TIME", all, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("dbo.FddQueryAudit", all, StringComparison.OrdinalIgnoreCase);

        foreach (Match match in CreatedTablePattern().Matches(all))
        {
            Assert.StartsWith("FddAgent", match.Groups[1].Value, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Readiness_script_is_read_only_and_checks_every_required_object()
    {
        var readiness = File.ReadAllText(Path.Combine(V2Directory(), "003-readiness-check.sql"));

        foreach (var table in RequiredTables)
        {
            Assert.Contains($"OBJECT_ID(N'dbo.{table}', N'U')", readiness, StringComparison.Ordinal);
        }

        Assert.Contains("SchemaVersion = 2", readiness, StringComparison.Ordinal);
        Assert.Contains("ScriptId = N'004-enable-bounded-multi-tool-turns'", readiness, StringComparison.Ordinal);
        Assert.DoesNotContain("INSERT ", readiness, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("UPDATE ", readiness, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DELETE ", readiness, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CREATE ", readiness, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DROP ", readiness, StringComparison.OrdinalIgnoreCase);
    }

    private static string V2Directory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "database", "v2");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate database/v2.");
    }

    [GeneratedRegex(@"CREATE\s+TABLE\s+dbo\.([A-Za-z0-9_]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CreatedTablePattern();
}
