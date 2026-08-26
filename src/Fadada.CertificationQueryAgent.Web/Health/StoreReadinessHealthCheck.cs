// Checks only the dedicated Agent schema and never creates tables or probes PSP business objects.
using Fadada.CertificationQueryAgent.Infrastructure.SqlServer2012;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Fadada.CertificationQueryAgent.Web.Health;

/// <summary>
/// 检查持久化配置和 Schema 就绪状态，为部署探针提供不含秘密的结果。
/// </summary>
public sealed class StoreReadinessHealthCheck(IConfiguration configuration) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        _ = context;
        var connectionString = configuration.GetConnectionString("FddDomainAgent") ??
            Environment.GetEnvironmentVariable("FDD_STORE_CONNECTION_STRING");
        var profileValue = configuration["Persistence:Profile"] ??
            Environment.GetEnvironmentVariable("FDD_STORE_PROFILE");
        if (string.IsNullOrWhiteSpace(connectionString) ||
            !Enum.TryParse<SqlPersistenceProfile>(profileValue, ignoreCase: false, out var profile))
        {
            return HealthCheckResult.Unhealthy("CONFIG_STORE_INVALID");
        }

        try
        {
            var factory = new SqlServerConnectionFactory(new SqlServer2012Options(connectionString, profile));
            var readiness = await factory.CheckReadinessAsync(cancellationToken).ConfigureAwait(false);
            return readiness.IsReady
                ? HealthCheckResult.Healthy("READY")
                : HealthCheckResult.Unhealthy(readiness.ErrorCode ?? "STORE_NOT_READY");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return HealthCheckResult.Unhealthy("STORE_READINESS_FAILED");
        }
    }
}
