using System.Data;
using Fadada.CertificationQueryAgent.Application.Persistence;
using Microsoft.Data.SqlClient;

namespace Fadada.CertificationQueryAgent.Infrastructure.SqlServer2012;

/// <summary>
/// 以有限批次删除超过保留期的数据，并维持依赖表的清理顺序。
/// </summary>
public sealed class SqlServerDataLifecycleStore(
    SqlServerConnectionFactory connectionFactory) : IDataLifecycleStore
{
    public async ValueTask<MaintenanceCleanupResult> CleanupAsync(
        MaintenanceCleanupRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.RunId == Guid.Empty || request.BatchSize is < 1 or > 1000 ||
            request.ArchivedConversationCutoffUtc >= request.StartedAtUtc)
        {
            throw new ArgumentException("Maintenance cleanup request is invalid.", nameof(request));
        }

        try
        {
            await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandTimeout = connectionFactory.Options.CommandTimeoutSeconds;
            command.CommandText = """
                INSERT dbo.FddAgentMaintenanceRun
                    (Id, Operation, ExaminedRows, DeletedRows, Status, StartedAtUtc)
                VALUES
                    (@runId, N'V2RetentionCleanup', 0, 0, N'Started', @startedAtUtc);

                DECLARE @remaining INT = @batchSize;
                DECLARE @diagnostics INT = 0;
                DECLARE @sessions INT = 0;
                DECLARE @messages INT = 0;

                ;WITH expired AS
                (
                    SELECT TOP (@remaining) Id
                    FROM dbo.FddAgentDiagnosticPayload WITH (READPAST, UPDLOCK, ROWLOCK)
                    WHERE ExpiresAtUtc <= @diagnosticCutoffUtc
                    ORDER BY ExpiresAtUtc, Id
                )
                DELETE payload
                FROM dbo.FddAgentDiagnosticPayload AS payload
                INNER JOIN expired ON expired.Id = payload.Id;
                SET @diagnostics = @@ROWCOUNT;
                SET @remaining = @remaining - @diagnostics;

                ;WITH expired AS
                (
                    SELECT TOP (@remaining) state.ConversationId
                    FROM dbo.FddAgentSessionState AS state WITH (READPAST, UPDLOCK, ROWLOCK)
                    INNER JOIN dbo.FddAgentConversation AS conversation ON conversation.Id = state.ConversationId
                    WHERE conversation.Status = N'Archived'
                      AND conversation.ArchivedAtUtc <= @archivedCutoffUtc
                    ORDER BY conversation.ArchivedAtUtc, state.ConversationId
                )
                DELETE state
                FROM dbo.FddAgentSessionState AS state
                INNER JOIN expired ON expired.ConversationId = state.ConversationId;
                SET @sessions = @@ROWCOUNT;
                SET @remaining = @remaining - @sessions;

                ;WITH expired AS
                (
                    SELECT TOP (@remaining) message.Id
                    FROM dbo.FddAgentMessage AS message WITH (READPAST, UPDLOCK, ROWLOCK)
                    INNER JOIN dbo.FddAgentConversation AS conversation ON conversation.Id = message.ConversationId
                    WHERE conversation.Status = N'Archived'
                      AND conversation.ArchivedAtUtc <= @archivedCutoffUtc
                    ORDER BY conversation.ArchivedAtUtc, message.Id
                )
                DELETE message
                FROM dbo.FddAgentMessage AS message
                INNER JOIN expired ON expired.Id = message.Id;
                SET @messages = @@ROWCOUNT;

                UPDATE dbo.FddAgentMaintenanceRun
                SET ExaminedRows = @diagnostics + @sessions + @messages,
                    DeletedRows = @diagnostics + @sessions + @messages,
                    Status = N'Succeeded',
                    CompletedAtUtc = SYSUTCDATETIME()
                WHERE Id = @runId AND Status = N'Started';

                SELECT @diagnostics, @sessions, @messages;
                """;
            SqlParameters.Add(command.Parameters, "@runId", SqlDbType.UniqueIdentifier, request.RunId);
            SqlParameters.Add(command.Parameters, "@batchSize", SqlDbType.Int, request.BatchSize);
            SqlParameters.Add(command.Parameters, "@diagnosticCutoffUtc", SqlDbType.DateTime2, SqlParameters.Utc(request.DiagnosticExpiryCutoffUtc));
            SqlParameters.Add(command.Parameters, "@archivedCutoffUtc", SqlDbType.DateTime2, SqlParameters.Utc(request.ArchivedConversationCutoffUtc));
            SqlParameters.Add(command.Parameters, "@startedAtUtc", SqlDbType.DateTime2, SqlParameters.Utc(request.StartedAtUtc));
            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException("MAINTENANCE_RESULT_MISSING");
            }

            var result = new MaintenanceCleanupResult(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2));
            await reader.DisposeAsync().ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch (SqlException exception)
        {
            await TryRecordFailureAsync(request, cancellationToken).ConfigureAwait(false);
            throw new SqlPersistenceException("MAINTENANCE_CLEANUP_FAILED", exception);
        }
    }

    private async ValueTask TryRecordFailureAsync(
        MaintenanceCleanupRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandTimeout = connectionFactory.Options.CommandTimeoutSeconds;
            command.CommandText = """
                IF NOT EXISTS (SELECT 1 FROM dbo.FddAgentMaintenanceRun WHERE Id = @runId)
                BEGIN
                    INSERT dbo.FddAgentMaintenanceRun
                        (Id, Operation, ExaminedRows, DeletedRows, Status, StartedAtUtc, CompletedAtUtc, SafeErrorCode)
                    VALUES
                        (@runId, N'V2RetentionCleanup', 0, 0, N'Failed', @startedAtUtc, SYSUTCDATETIME(), N'MAINTENANCE_SQL_FAILED');
                END;
                """;
            SqlParameters.Add(command.Parameters, "@runId", SqlDbType.UniqueIdentifier, request.RunId);
            SqlParameters.Add(command.Parameters, "@startedAtUtc", SqlDbType.DateTime2, SqlParameters.Utc(request.StartedAtUtc));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // The original safe persistence exception remains authoritative.
        }
    }
}
