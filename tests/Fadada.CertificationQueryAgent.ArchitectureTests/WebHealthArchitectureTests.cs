// Locks liveness and loopback-only readiness behavior at the Web composition boundary.
namespace Fadada.CertificationQueryAgent.ArchitectureTests;

/// <summary>
/// 验证 WebHealthArchitectureTests 所覆盖的行为、边界条件和安全回归约束。
/// </summary>
public sealed class WebHealthArchitectureTests
{
    [Fact]
    public void Health_endpoints_keep_liveness_anonymous_and_readiness_restricted()
    {
        var program = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "Fadada.CertificationQueryAgent.Web", "Program.cs"));

        Assert.Contains("/health/live", program, StringComparison.Ordinal);
        Assert.Contains("Predicate = _ => false", program, StringComparison.Ordinal);
        Assert.Contains("/health/ready", program, StringComparison.Ordinal);
        Assert.Contains("RequireAuthorization(\"ReadyHealth\")", program, StringComparison.Ordinal);
        Assert.DoesNotContain("report.Entries", program, StringComparison.Ordinal);
    }

    [Fact]
    public void Telemetry_configuration_forbids_sensitive_ai_and_sql_parameter_capture()
    {
        var root = FindRepositoryRoot();
        var agentTelemetry = File.ReadAllText(Path.Combine(
            root, "src", "Fadada.CertificationQueryAgent.AgentHost", "Telemetry", "AgentTelemetry.cs"));
        var registration = File.ReadAllText(Path.Combine(
            root, "src", "Fadada.CertificationQueryAgent.Infrastructure", "Telemetry", "OpenTelemetryRegistration.cs"));

        Assert.Contains("EnableSensitiveData = false", agentTelemetry, StringComparison.Ordinal);
        Assert.Contains("CaptureSensitiveContent", registration, StringComparison.Ordinal);
        Assert.DoesNotContain("EnrichWithSqlCommand", registration, StringComparison.Ordinal);
        Assert.DoesNotContain("UserMessage", agentTelemetry, StringComparison.Ordinal);
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
