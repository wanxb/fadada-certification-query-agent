// Registers opt-in telemetry with sensitive-content capture disabled by default.
using Microsoft.Extensions.Configuration;
using Fadada.CertificationQueryAgent.Application.Observability;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Fadada.CertificationQueryAgent.Infrastructure.Telemetry;

/// <summary>
/// 集中表达 CertificationQueryTelemetryOptions 的配置和约束，使默认值、验证规则与运行行为保持一致。
/// </summary>
public sealed record CertificationQueryTelemetryOptions(
    bool Enabled,
    string ServiceName,
    Uri? OtlpEndpoint,
    double SamplingRatio,
    bool CaptureSensitiveContent)
{
    public static CertificationQueryTelemetryOptions FromConfiguration(IConfiguration configuration)
    {
        var section = configuration.GetSection("OpenTelemetry");
        var endpointValue = section["OtlpEndpoint"];
        return new CertificationQueryTelemetryOptions(
            section.GetValue("Enabled", true),
            section["ServiceName"] ?? TelemetryNames.ServiceName,
            string.IsNullOrWhiteSpace(endpointValue) ? null : new Uri(endpointValue, UriKind.Absolute),
            section.GetValue("SamplingRatio", 1d),
            section.GetValue("CaptureSensitiveContent", false));
    }

    public void Validate(IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        if (string.IsNullOrWhiteSpace(ServiceName) || ServiceName.Length > 128 ||
            SamplingRatio is < 0 or > 1 || CaptureSensitiveContent)
        {
            throw new InvalidOperationException("CONFIG_TELEMETRY_INVALID");
        }

        if (OtlpEndpoint is not null &&
            OtlpEndpoint.Scheme is not ("https" or "http"))
        {
            throw new InvalidOperationException("CONFIG_OTLP_ENDPOINT_INVALID");
        }

        if (!environment.IsDevelopment() && OtlpEndpoint is { Scheme: "http" } && !OtlpEndpoint.IsLoopback)
        {
            throw new InvalidOperationException("CONFIG_OTLP_TRANSPORT_INSECURE");
        }
    }
}

/// <summary>
/// 集中注册 OpenTelemetryRegistration 对应的服务边界，保持启动装配和安全策略一致。
/// </summary>
public static class OpenTelemetryRegistration
{
    public static IServiceCollection AddCertificationQueryTelemetry(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        var options = CertificationQueryTelemetryOptions.FromConfiguration(configuration);
        options.Validate(environment);
        services.AddSingleton(options);
        if (!options.Enabled)
        {
            return services;
        }

        var builder = services
            .AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(options.ServiceName))
            .WithTracing(tracing =>
            {
                tracing
                    .SetSampler(new TraceIdRatioBasedSampler(options.SamplingRatio))
                    .AddSource(TelemetryNames.ActivitySource, TelemetryNames.GenAiSource)
                    .AddAspNetCoreInstrumentation(instrumentation => instrumentation.RecordException = false)
                    .AddHttpClientInstrumentation(instrumentation => instrumentation.RecordException = false)
                    .AddSqlClientInstrumentation(instrumentation =>
                    {
                        instrumentation.RecordException = false;
                    });
                if (options.OtlpEndpoint is not null)
                {
                    tracing.AddOtlpExporter(exporter => exporter.Endpoint = options.OtlpEndpoint);
                }
            })
            .WithMetrics(metrics =>
            {
                metrics
                    .AddMeter(TelemetryNames.Meter, TelemetryNames.GenAiSource)
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation();
                if (options.OtlpEndpoint is not null)
                {
                    metrics.AddOtlpExporter(exporter => exporter.Endpoint = options.OtlpEndpoint);
                }
            });
        _ = builder;
        return services;
    }
}
