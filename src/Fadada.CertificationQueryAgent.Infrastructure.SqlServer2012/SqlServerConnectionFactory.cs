// Centralizes validated connection creation so repositories cannot weaken profile security settings.
using System.Data;
using Microsoft.Data.SqlClient;

namespace Fadada.CertificationQueryAgent.Infrastructure.SqlServer2012;

/// <summary>
/// 依据持久化 Profile 校验数据库目标和连接安全后创建 SQL Server 连接。
/// </summary>
public sealed class SqlServerConnectionFactory
{
    private readonly string connectionString;

    public SqlServerConnectionFactory(SqlServer2012Options options)
    {
        Options = options ?? throw new ArgumentNullException(nameof(options));
        connectionString = options.Validate().ConnectionString;
    }

    public SqlServer2012Options Options { get; }

    public async ValueTask<SqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask<SqlSchemaReadiness> CheckReadinessAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandTimeout = Options.CommandTimeoutSeconds;
            command.CommandText = """
                SELECT
                    CONVERT(NVARCHAR(128), SERVERPROPERTY(N'ProductVersion')),
                    DB_NAME(),
                    compatibility_level
                FROM sys.databases
                WHERE name = DB_NAME();
                """;
            int major;
            string database;
            int compatibility;
            await using (var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken).ConfigureAwait(false))
            {
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    return new SqlSchemaReadiness(false, 0, string.Empty, 0, null, "STORE_METADATA_MISSING");
                }

                major = Version.Parse(reader.GetString(0)).Major;
                database = reader.GetString(1);
                compatibility = reader.GetByte(2);
            }

            var profileValid = Options.Profile == SqlPersistenceProfile.LabSqlServer2012
                ? major == 11 && string.Equals(database, SqlServer2012Options.ApprovedLabDatabase, StringComparison.Ordinal)
                : major >= 14;
            command.CommandText = "SELECT OBJECT_ID(N'dbo.FddAgentSchemaVersion', N'U');";
            command.Parameters.Clear();
            var schemaObjectId = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (!profileValid || schemaObjectId is null or DBNull)
            {
                return new SqlSchemaReadiness(
                    false,
                    major,
                    database,
                    compatibility,
                    null,
                    profileValid ? "STORE_SCHEMA_NOT_READY" : "STORE_PROFILE_REJECTED");
            }

            command.CommandText = "SELECT SchemaVersion, ScriptId FROM dbo.FddAgentSchemaVersion WHERE Component = N'FddDomainAgent';";
            int? schemaVersion = null;
            string? scriptId = null;
            await using (var schemaReader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken).ConfigureAwait(false))
            {
                if (await schemaReader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    schemaVersion = schemaReader.GetInt32(0);
                    scriptId = schemaReader.GetString(1);
                }
            }

            var ready = profileValid &&
                schemaVersion == 2 &&
                string.Equals(scriptId, "004-enable-bounded-multi-tool-turns", StringComparison.Ordinal);
            return new SqlSchemaReadiness(
                ready,
                major,
                database,
                compatibility,
                schemaVersion,
                ready ? null : profileValid ? "STORE_SCHEMA_NOT_READY" : "STORE_PROFILE_REJECTED");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SqlException)
        {
            return new SqlSchemaReadiness(false, 0, string.Empty, 0, null, "STORE_CONNECTION_FAILED");
        }
    }
}

/// <summary>
/// 统一创建显式类型和长度的 SQL 参数，避免隐式推断造成兼容或截断问题。
/// </summary>
internal static class SqlParameters
{
    public static SqlParameter Add(SqlParameterCollection parameters, string name, SqlDbType type, object? value, int size = 0)
    {
        var parameter = size == 0 ? parameters.Add(name, type) : parameters.Add(name, type, size);
        parameter.Value = value ?? DBNull.Value;
        return parameter;
    }

    public static DateTime Utc(DateTimeOffset value) => value.UtcDateTime;
}
