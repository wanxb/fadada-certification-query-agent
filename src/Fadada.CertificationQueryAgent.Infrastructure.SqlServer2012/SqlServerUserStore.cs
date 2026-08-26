// Stores password hashes, lockout state, and security stamps with optimistic concurrency checks.
using System.Data;
using Fadada.CertificationQueryAgent.Application.Authentication;
using Fadada.CertificationQueryAgent.Application.Common;
using Microsoft.Data.SqlClient;

namespace Fadada.CertificationQueryAgent.Infrastructure.SqlServer2012;

/// <summary>
/// 在 SQL Server 中持久化本地账号和并发令牌，保持认证状态更新的原子性。
/// </summary>
public sealed class SqlServerUserStore(SqlServerConnectionFactory connectionFactory) : IUserStore
{
    public ValueTask<UserAccount?> GetByNormalizedNameAsync(
        string normalizedUserName,
        CancellationToken cancellationToken) =>
        GetAsync(UserLookup.NormalizedName, normalizedUserName, SqlDbType.NVarChar, cancellationToken);

    public ValueTask<UserAccount?> GetByIdAsync(UserId userId, CancellationToken cancellationToken) =>
        GetAsync(UserLookup.Id, userId.Value, SqlDbType.UniqueIdentifier, cancellationToken);

    public async ValueTask CreateAsync(
        UserAccount account,
        AccountMutationAudit audit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(account);
        ValidateAudit(audit);
        try
        {
            await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandTimeout = connectionFactory.Options.CommandTimeoutSeconds;
            command.CommandText = """
                INSERT dbo.FddAgentUser
                    (Id, UserName, NormalizedUserName, DisplayName, PasswordHash, SecurityStamp,
                     IsActive, AccessFailedCount, LockoutEndUtc, LastLoginAtUtc, CreatedAtUtc, UpdatedAtUtc)
                VALUES
                    (@id, @userName, @normalizedUserName, @displayName, @passwordHash, @securityStamp,
                     @isActive, @accessFailedCount, @lockoutEndUtc, @lastLoginAtUtc, @now, @now);
                """;
            SqlParameters.Add(command.Parameters, "@id", SqlDbType.UniqueIdentifier, account.Id.Value);
            SqlParameters.Add(command.Parameters, "@userName", SqlDbType.NVarChar, account.UserName, 128);
            SqlParameters.Add(command.Parameters, "@normalizedUserName", SqlDbType.NVarChar, account.NormalizedUserName, 128);
            SqlParameters.Add(command.Parameters, "@displayName", SqlDbType.NVarChar, account.DisplayName, 128);
            SqlParameters.Add(command.Parameters, "@passwordHash", SqlDbType.NVarChar, account.PasswordHash, 1024);
            SqlParameters.Add(command.Parameters, "@securityStamp", SqlDbType.NVarChar, account.SecurityStamp, 128);
            SqlParameters.Add(command.Parameters, "@isActive", SqlDbType.Bit, account.IsActive);
            SqlParameters.Add(command.Parameters, "@accessFailedCount", SqlDbType.Int, account.AccessFailedCount);
            SqlParameters.Add(command.Parameters, "@lockoutEndUtc", SqlDbType.DateTime2, account.LockoutEndUtc is null ? null : SqlParameters.Utc(account.LockoutEndUtc.Value));
            SqlParameters.Add(command.Parameters, "@lastLoginAtUtc", SqlDbType.DateTime2, account.LastLoginAtUtc is null ? null : SqlParameters.Utc(account.LastLoginAtUtc.Value));
            SqlParameters.Add(command.Parameters, "@now", SqlDbType.DateTime2, DateTime.UtcNow);
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new InvalidOperationException("STORE_USER_CREATE_INCOMPLETE");
            }

            await InsertAuditAsync(connection, transaction, account.Id, audit, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqlException exception)
        {
            throw new SqlPersistenceException("STORE_USER_CREATE_FAILED", exception);
        }
    }

    public ValueTask<bool> UpdateAuthenticationStateAsync(
        UserId userId,
        int accessFailedCount,
        DateTimeOffset? lockoutEndUtc,
        DateTimeOffset? lastLoginAtUtc,
        byte[] expectedRowVersion,
        AccountMutationAudit audit,
        CancellationToken cancellationToken) =>
        UpdateAsync(
            UserUpdate.AuthenticationState,
            userId,
            expectedRowVersion,
            audit,
            command =>
            {
                SqlParameters.Add(command.Parameters, "@accessFailedCount", SqlDbType.Int, accessFailedCount);
                SqlParameters.Add(command.Parameters, "@lockoutEndUtc", SqlDbType.DateTime2, lockoutEndUtc is null ? null : SqlParameters.Utc(lockoutEndUtc.Value));
                SqlParameters.Add(command.Parameters, "@lastLoginAtUtc", SqlDbType.DateTime2, lastLoginAtUtc is null ? null : SqlParameters.Utc(lastLoginAtUtc.Value));
            },
            cancellationToken);

    public ValueTask<bool> UpdateCredentialsAsync(
        UserId userId,
        string passwordHash,
        string securityStamp,
        byte[] expectedRowVersion,
        AccountMutationAudit audit,
        CancellationToken cancellationToken) =>
        UpdateAsync(
            UserUpdate.Credentials,
            userId,
            expectedRowVersion,
            audit,
            command =>
            {
                SqlParameters.Add(command.Parameters, "@passwordHash", SqlDbType.NVarChar, passwordHash, 1024);
                SqlParameters.Add(command.Parameters, "@securityStamp", SqlDbType.NVarChar, securityStamp, 128);
            },
            cancellationToken);

    public ValueTask<bool> SetActiveAsync(
        UserId userId,
        bool isActive,
        string securityStamp,
        byte[] expectedRowVersion,
        AccountMutationAudit audit,
        CancellationToken cancellationToken) =>
        UpdateAsync(
            UserUpdate.ActiveState,
            userId,
            expectedRowVersion,
            audit,
            command =>
            {
                SqlParameters.Add(command.Parameters, "@isActive", SqlDbType.Bit, isActive);
                SqlParameters.Add(command.Parameters, "@securityStamp", SqlDbType.NVarChar, securityStamp, 128);
            },
            cancellationToken);

    private async ValueTask<UserAccount?> GetAsync(
        UserLookup lookup,
        object value,
        SqlDbType valueType,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandTimeout = connectionFactory.Options.CommandTimeoutSeconds;
            command.CommandText = lookup switch
            {
                UserLookup.NormalizedName => """
                    SELECT Id, UserName, NormalizedUserName, DisplayName, PasswordHash, SecurityStamp,
                           IsActive, AccessFailedCount, LockoutEndUtc, LastLoginAtUtc, RowVersion
                    FROM dbo.FddAgentUser
                    WHERE NormalizedUserName = @value;
                    """,
                UserLookup.Id => """
                    SELECT Id, UserName, NormalizedUserName, DisplayName, PasswordHash, SecurityStamp,
                           IsActive, AccessFailedCount, LockoutEndUtc, LastLoginAtUtc, RowVersion
                    FROM dbo.FddAgentUser
                    WHERE Id = @value;
                    """,
                _ => throw new ArgumentOutOfRangeException(nameof(lookup))
            };
            SqlParameters.Add(command.Parameters, "@value", valueType, value, valueType == SqlDbType.NVarChar ? 128 : 0);
            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            return new UserAccount(
                new UserId(reader.GetGuid(0)),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetBoolean(6),
                reader.GetInt32(7),
                reader.IsDBNull(8) ? null : new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(8), DateTimeKind.Utc)),
                reader.IsDBNull(9) ? null : new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(9), DateTimeKind.Utc)),
                reader.GetFieldValue<byte[]>(10));
        }
        catch (SqlException exception)
        {
            throw new SqlPersistenceException("STORE_USER_READ_FAILED", exception);
        }
    }

    private async ValueTask<bool> UpdateAsync(
        UserUpdate update,
        UserId userId,
        byte[] expectedRowVersion,
        AccountMutationAudit audit,
        Action<SqlCommand> addParameters,
        CancellationToken cancellationToken)
    {
        if (expectedRowVersion is not { Length: 8 })
        {
            throw new ArgumentException("Expected row version must contain eight bytes.", nameof(expectedRowVersion));
        }
        ValidateAudit(audit);

        try
        {
            await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandTimeout = connectionFactory.Options.CommandTimeoutSeconds;
            command.CommandText = update switch
            {
                UserUpdate.AuthenticationState => """
                    UPDATE dbo.FddAgentUser
                    SET AccessFailedCount = @accessFailedCount,
                        LockoutEndUtc = @lockoutEndUtc,
                        LastLoginAtUtc = @lastLoginAtUtc,
                        UpdatedAtUtc = SYSUTCDATETIME()
                    WHERE Id = @id AND RowVersion = @rowVersion;
                    """,
                UserUpdate.Credentials => """
                    UPDATE dbo.FddAgentUser
                    SET PasswordHash = @passwordHash,
                        SecurityStamp = @securityStamp,
                        AccessFailedCount = 0,
                        LockoutEndUtc = NULL,
                        UpdatedAtUtc = SYSUTCDATETIME()
                    WHERE Id = @id AND RowVersion = @rowVersion;
                    """,
                UserUpdate.ActiveState => """
                    UPDATE dbo.FddAgentUser
                    SET IsActive = @isActive,
                        SecurityStamp = @securityStamp,
                        AccessFailedCount = 0,
                        LockoutEndUtc = NULL,
                        UpdatedAtUtc = SYSUTCDATETIME()
                    WHERE Id = @id AND RowVersion = @rowVersion;
                    """,
                _ => throw new ArgumentOutOfRangeException(nameof(update))
            };
            SqlParameters.Add(command.Parameters, "@id", SqlDbType.UniqueIdentifier, userId.Value);
            SqlParameters.Add(command.Parameters, "@rowVersion", SqlDbType.Binary, expectedRowVersion, 8);
            addParameters(command);
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return false;
            }

            await InsertAuditAsync(connection, transaction, userId, audit, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (SqlException exception)
        {
            throw new SqlPersistenceException("STORE_USER_UPDATE_FAILED", exception);
        }
    }

    private async ValueTask InsertAuditAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        UserId userId,
        AccountMutationAudit audit,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = connectionFactory.Options.CommandTimeoutSeconds;
        command.CommandText = """
            INSERT dbo.FddAgentSecurityEvent (Id, TargetUserId, EventType, Actor, OccurredAtUtc)
            VALUES (@id, @targetUserId, @eventType, @actor, @occurredAtUtc);
            """;
        SqlParameters.Add(command.Parameters, "@id", SqlDbType.UniqueIdentifier, audit.Id);
        SqlParameters.Add(command.Parameters, "@targetUserId", SqlDbType.UniqueIdentifier, userId.Value);
        SqlParameters.Add(command.Parameters, "@eventType", SqlDbType.NVarChar, audit.EventType, 32);
        SqlParameters.Add(command.Parameters, "@actor", SqlDbType.NVarChar, audit.Actor, 128);
        SqlParameters.Add(command.Parameters, "@occurredAtUtc", SqlDbType.DateTime2, SqlParameters.Utc(audit.OccurredAtUtc));
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException("STORE_SECURITY_AUDIT_INCOMPLETE");
        }
    }

    private static void ValidateAudit(AccountMutationAudit audit)
    {
        ArgumentNullException.ThrowIfNull(audit);
        if (audit.Id == Guid.Empty || string.IsNullOrWhiteSpace(audit.EventType) || audit.EventType.Length > 32 ||
            string.IsNullOrWhiteSpace(audit.Actor) || audit.Actor.Length > 128)
        {
            throw new ArgumentException("Account mutation audit is invalid.", nameof(audit));
        }
    }

    /// <summary>
    /// 定义 UserLookup 的受控状态集合，避免跨层使用未校验的自由文本。
    /// </summary>
    private enum UserLookup
    {
        NormalizedName,
        Id
    }

    /// <summary>
    /// 定义 UserUpdate 的受控状态集合，避免跨层使用未校验的自由文本。
    /// </summary>
    private enum UserUpdate
    {
        AuthenticationState,
        Credentials,
        ActiveState
    }
}
