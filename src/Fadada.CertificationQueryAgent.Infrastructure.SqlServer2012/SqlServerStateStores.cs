// Persists protected session state and encrypted diagnostics separately from conversation content.
using System.Data;
using Fadada.CertificationQueryAgent.Application.Common;
using Fadada.CertificationQueryAgent.Application.Persistence;
using Microsoft.Data.SqlClient;

namespace Fadada.CertificationQueryAgent.Infrastructure.SqlServer2012;

/// <summary>
/// 持久化模型会话状态，并通过版本控制避免并发覆盖。
/// </summary>
public sealed class SqlServerSessionStateStore(SqlServerConnectionFactory connectionFactory) : IAgentSessionStateStore
{
    public async ValueTask<SessionState?> GetAsync(
        ConversationId conversationId,
        UserId userId,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandTimeout = connectionFactory.Options.CommandTimeoutSeconds;
            command.CommandText = """
                SELECT s.ConversationId, s.Format, s.StateVersion, s.ProtectedPayload, s.RowVersion
                FROM dbo.FddAgentSessionState AS s
                INNER JOIN dbo.FddAgentConversation AS c ON c.Id = s.ConversationId
                WHERE s.ConversationId = @conversationId AND c.UserId = @userId;
                """;
            SqlParameters.Add(command.Parameters, "@conversationId", SqlDbType.UniqueIdentifier, conversationId.Value);
            SqlParameters.Add(command.Parameters, "@userId", SqlDbType.UniqueIdentifier, userId.Value);
            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            return new SessionState(
                new ConversationId(reader.GetGuid(0)),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetFieldValue<byte[]>(3),
                reader.GetFieldValue<byte[]>(4));
        }
        catch (SqlException exception)
        {
            throw new SqlPersistenceException("STORE_SESSION_READ_FAILED", exception);
        }
    }

    public async ValueTask SaveAsync(
        SessionState state,
        UserId userId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (string.IsNullOrWhiteSpace(state.Format) || state.Format.Length > 64 ||
            string.IsNullOrWhiteSpace(state.Version) || state.Version.Length > 64 ||
            state.ProtectedPayload.Length == 0 || state.ProtectedPayload.Length > 1_048_576 ||
            state.RowVersion.Length is not (0 or 8))
        {
            throw new ArgumentException("Session state is invalid.", nameof(state));
        }

        try
        {
            await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = (SqlTransaction)await connection
                .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
                .ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandTimeout = connectionFactory.Options.CommandTimeoutSeconds;
            if (state.RowVersion.Length == 0)
            {
                command.CommandText = """
                    INSERT dbo.FddAgentSessionState (ConversationId, Format, StateVersion, ProtectedPayload, UpdatedAtUtc)
                    SELECT c.Id, @format, @version, @payload, SYSUTCDATETIME()
                    FROM dbo.FddAgentConversation AS c WITH (UPDLOCK, HOLDLOCK)
                    WHERE c.Id = @conversationId AND c.UserId = @userId AND c.Status = N'Active'
                      AND NOT EXISTS (SELECT 1 FROM dbo.FddAgentSessionState WHERE ConversationId = c.Id);
                    """;
            }
            else
            {
                command.CommandText = """
                    UPDATE s
                    SET Format = @format, StateVersion = @version, ProtectedPayload = @payload, UpdatedAtUtc = SYSUTCDATETIME()
                    FROM dbo.FddAgentSessionState AS s
                    INNER JOIN dbo.FddAgentConversation AS c ON c.Id = s.ConversationId
                    WHERE s.ConversationId = @conversationId AND c.UserId = @userId AND c.Status = N'Active'
                      AND s.RowVersion = @rowVersion;
                    """;
                SqlParameters.Add(command.Parameters, "@rowVersion", SqlDbType.Binary, state.RowVersion, 8);
            }

            SqlParameters.Add(command.Parameters, "@conversationId", SqlDbType.UniqueIdentifier, state.ConversationId.Value);
            SqlParameters.Add(command.Parameters, "@userId", SqlDbType.UniqueIdentifier, userId.Value);
            SqlParameters.Add(command.Parameters, "@format", SqlDbType.NVarChar, state.Format, 64);
            SqlParameters.Add(command.Parameters, "@version", SqlDbType.NVarChar, state.Version, 64);
            SqlParameters.Add(command.Parameters, "@payload", SqlDbType.VarBinary, state.ProtectedPayload, -1);
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new PersistenceConcurrencyException("STORE_SESSION_CONFLICT");
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (PersistenceConcurrencyException)
        {
            throw;
        }
        catch (SqlException exception)
        {
            throw new SqlPersistenceException("STORE_SESSION_WRITE_FAILED", exception);
        }
    }
}

/// <summary>
/// 保存已加密的诊断载荷及保留期限，数据库中不落明文正文。
/// </summary>
public sealed class SqlServerDiagnosticPayloadStore(SqlServerConnectionFactory connectionFactory) : IDiagnosticPayloadStore
{
    public async ValueTask SaveAsync(DiagnosticPayload payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var now = DateTimeOffset.UtcNow;
        if (payload.ProtectedPayload.Length == 0 || payload.ProtectedPayload.Length > 1_048_576 ||
            payload.ExpiresAtUtc <= now || payload.ExpiresAtUtc > now.AddDays(7) ||
            payload.OwnerType is not ("Turn" or "ModelCall" or "ToolCall" or "ExternalCall"))
        {
            throw new ArgumentException("Diagnostic payload is invalid.", nameof(payload));
        }

        try
        {
            await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandTimeout = connectionFactory.Options.CommandTimeoutSeconds;
            command.CommandText = """
                INSERT dbo.FddAgentDiagnosticPayload
                    (Id, UserId, OwnerType, OwnerId, ProtectedPayload, ExpiresAtUtc, CreatedAtUtc)
                VALUES
                    (@id, @userId, @ownerType, @ownerId, @payload, @expiresAtUtc, @createdAtUtc);
                """;
            SqlParameters.Add(command.Parameters, "@id", SqlDbType.UniqueIdentifier, payload.Id);
            SqlParameters.Add(command.Parameters, "@userId", SqlDbType.UniqueIdentifier, payload.UserId.Value);
            SqlParameters.Add(command.Parameters, "@ownerType", SqlDbType.NVarChar, payload.OwnerType, 32);
            SqlParameters.Add(command.Parameters, "@ownerId", SqlDbType.UniqueIdentifier, payload.OwnerId);
            SqlParameters.Add(command.Parameters, "@payload", SqlDbType.VarBinary, payload.ProtectedPayload, -1);
            SqlParameters.Add(command.Parameters, "@expiresAtUtc", SqlDbType.DateTime2, SqlParameters.Utc(payload.ExpiresAtUtc));
            SqlParameters.Add(command.Parameters, "@createdAtUtc", SqlDbType.DateTime2, SqlParameters.Utc(now));
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new InvalidOperationException("STORE_DIAGNOSTIC_WRITE_INCOMPLETE");
            }
        }
        catch (SqlException exception)
        {
            throw new SqlPersistenceException("STORE_DIAGNOSTIC_WRITE_FAILED", exception);
        }
    }

    public async ValueTask<DiagnosticPayload?> GetAsync(
        Guid payloadId,
        UserId userId,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandTimeout = connectionFactory.Options.CommandTimeoutSeconds;
            command.CommandText = """
                SELECT Id, UserId, OwnerType, OwnerId, ProtectedPayload, ExpiresAtUtc
                FROM dbo.FddAgentDiagnosticPayload
                WHERE Id = @id AND UserId = @userId AND ExpiresAtUtc > SYSUTCDATETIME();
                """;
            SqlParameters.Add(command.Parameters, "@id", SqlDbType.UniqueIdentifier, payloadId);
            SqlParameters.Add(command.Parameters, "@userId", SqlDbType.UniqueIdentifier, userId.Value);
            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            return new DiagnosticPayload(
                reader.GetGuid(0),
                new UserId(reader.GetGuid(1)),
                reader.GetString(2),
                reader.GetGuid(3),
                reader.GetFieldValue<byte[]>(4),
                new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(5), DateTimeKind.Utc)));
        }
        catch (SqlException exception)
        {
            throw new SqlPersistenceException("STORE_DIAGNOSTIC_READ_FAILED", exception);
        }
    }

    public async ValueTask<int> DeleteExpiredAsync(
        DateTimeOffset expiresBeforeUtc,
        int batchSize,
        CancellationToken cancellationToken)
    {
        if (batchSize is < 1 or > 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(batchSize));
        }

        try
        {
            await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandTimeout = connectionFactory.Options.CommandTimeoutSeconds;
            command.CommandText = """
                ;WITH expired AS
                (
                    SELECT TOP (@batchSize) Id
                    FROM dbo.FddAgentDiagnosticPayload WITH (READPAST, UPDLOCK, ROWLOCK)
                    WHERE ExpiresAtUtc <= @expiresBeforeUtc
                    ORDER BY ExpiresAtUtc, Id
                )
                DELETE FROM expired;
                """;
            SqlParameters.Add(command.Parameters, "@batchSize", SqlDbType.Int, batchSize);
            SqlParameters.Add(command.Parameters, "@expiresBeforeUtc", SqlDbType.DateTime2, SqlParameters.Utc(expiresBeforeUtc));
            return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqlException exception)
        {
            throw new SqlPersistenceException("STORE_DIAGNOSTIC_DELETE_FAILED", exception);
        }
    }
}
