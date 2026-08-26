// Verifies telemetry defaults omit sensitive content and honor explicit endpoint configuration.
using Fadada.CertificationQueryAgent.Application.Auditing;
using Fadada.CertificationQueryAgent.Infrastructure.Telemetry;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace Fadada.CertificationQueryAgent.IntegrationTests;

/// <summary>
/// 验证 TelemetryConfigurationTests 所覆盖的行为、边界条件和安全回归约束。
/// </summary>
public sealed class TelemetryConfigurationTests
{
    [Fact]
    public void Production_rejects_sensitive_capture_and_insecure_remote_otlp()
    {
        var environment = new TestHostEnvironment(Environments.Production);

        Assert.Equal(
            "CONFIG_TELEMETRY_INVALID",
            Assert.Throws<InvalidOperationException>(() => new CertificationQueryTelemetryOptions(
                true,
                "Fadada.CertificationQueryAgent",
                null,
                1,
                true).Validate(environment)).Message);
        Assert.Equal(
            "CONFIG_OTLP_TRANSPORT_INSECURE",
            Assert.Throws<InvalidOperationException>(() => new CertificationQueryTelemetryOptions(
                true,
                "Fadada.CertificationQueryAgent",
                new Uri("http://collector.internal:4317"),
                1,
                false).Validate(environment)).Message);
    }

    [Fact]
    public void Model_cost_is_deterministic_and_rounded_to_storage_scale()
    {
        var pricing = new ModelPricing(2.5m, 10m);

        Assert.Equal(0.00004500m, pricing.Estimate(10, 2));
    }

    /// <summary>
    /// 为所属测试提供可观测且无外部副作用的 TestHostEnvironment 测试替身。
    /// </summary>
    private sealed class TestHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;

        public string ApplicationName { get; set; } = "Fadada.CertificationQueryAgent.Tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
