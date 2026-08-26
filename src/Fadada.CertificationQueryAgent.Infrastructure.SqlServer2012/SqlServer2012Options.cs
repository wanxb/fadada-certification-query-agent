// Separates the explicitly accepted SQL Server 2012 lab profile from stricter production settings.
using Microsoft.Data.SqlClient;

namespace Fadada.CertificationQueryAgent.Infrastructure.SqlServer2012;

/// <summary>
/// 定义 SqlPersistenceProfile 的受控状态集合，避免跨层使用未校验的自由文本。
/// </summary>
public enum SqlPersistenceProfile
{
    LabSqlServer2012,
    ProductionReference
}

/// <summary>
/// 集中表达 SqlServer2012Options 的配置和约束，使默认值、验证规则与运行行为保持一致。
/// </summary>
public sealed record SqlServer2012Options(
    string ConnectionString,
    SqlPersistenceProfile Profile,
    int CommandTimeoutSeconds = 30)
{
    public const string ApprovedLabServer = "localhost";
    public const string ApprovedLabDatabase = "FadadaAgentLab";

    public SqlConnectionStringBuilder Validate()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString))
        {
            throw new InvalidOperationException("STORE_CONNECTION_REQUIRED");
        }

        if (CommandTimeoutSeconds is < 1 or > 300)
        {
            throw new InvalidOperationException("STORE_COMMAND_TIMEOUT_INVALID");
        }

        var builder = new SqlConnectionStringBuilder(ConnectionString)
        {
            ApplicationName = "Fadada.CertificationQueryAgent.V2",
            ConnectTimeout = Math.Min(CommandTimeoutSeconds, 30),
            PersistSecurityInfo = false
        };
        if (string.IsNullOrWhiteSpace(builder.DataSource) || string.IsNullOrWhiteSpace(builder.InitialCatalog))
        {
            throw new InvalidOperationException("STORE_TARGET_REQUIRED");
        }

        if (Profile == SqlPersistenceProfile.LabSqlServer2012)
        {
            if (!string.Equals(builder.DataSource, ApprovedLabServer, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(builder.InitialCatalog, ApprovedLabDatabase, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("STORE_LAB_TARGET_REJECTED");
            }
        }
        else
        {
            if (string.Equals(builder.DataSource, ApprovedLabServer, StringComparison.OrdinalIgnoreCase) ||
                !builder.Encrypt ||
                builder.TrustServerCertificate)
            {
                throw new InvalidOperationException("STORE_PRODUCTION_TRANSPORT_REJECTED");
            }
        }

        return builder;
    }

    public override string ToString() =>
        $"SqlServer2012Options {{ Profile = {Profile}, ConnectionString = [REDACTED], CommandTimeoutSeconds = {CommandTimeoutSeconds} }}";
}

/// <summary>
/// 以不可变数据契约表达 SqlSchemaReadiness，确保跨层传递时字段语义和边界保持稳定。
/// </summary>
public sealed record SqlSchemaReadiness(
    bool IsReady,
    int ServerMajorVersion,
    string DatabaseName,
    int CompatibilityLevel,
    int? SchemaVersion,
    string? ErrorCode);

/// <summary>
/// 封装 SQL 持久化失败的稳定错误码，避免数据库细节泄露到 Web 层。
/// </summary>
public sealed class SqlPersistenceException(string errorCode, Exception innerException) : Exception(errorCode, innerException)
{
    public string ErrorCode { get; } = errorCode;
}
