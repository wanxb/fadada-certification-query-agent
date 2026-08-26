// Stores and lists conversations only through user-scoped queries to prevent horizontal access.
using System.Data;
using Fadada.CertificationQueryAgent.Application.Common;
using Fadada.CertificationQueryAgent.Application.Conversations;
using Microsoft.Data.SqlClient;

namespace Fadada.CertificationQueryAgent.Infrastructure.SqlServer2012;

/// <summary>
/// 按用户所有权读写会话和消息，防止跨账号枚举或修改会话。
/// </summary>
public sealed class SqlServerConversationStore(SqlServerConnectionFactory connectionFactory) : IConversationStore
{
    public async ValueTask<ConversationSummary> CreateAsync(
        UserId userId,
        string title,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(title) || title.Length > 200)
        {
            throw new ArgumentException("Conversation title is invalid.", nameof(title));
        }

        var id = ConversationId.New();
        var now = DateTimeOffset.UtcNow;
        try
        {
            await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandTimeout = connectionFactory.Options.CommandTimeoutSeconds;
            command.CommandText = """
                INSERT dbo.FddAgentConversation
                    (Id, UserId, Title, Status, NextSequenceNumber, CreatedAtUtc, UpdatedAtUtc, ArchivedAtUtc)
                OUTPUT inserted.RowVersion
                VALUES (@id, @userId, @title, N'Active', 1, @now, @now, NULL);
                """;
            SqlParameters.Add(command.Parameters, "@id", SqlDbType.UniqueIdentifier, id.Value);
            SqlParameters.Add(command.Parameters, "@userId", SqlDbType.UniqueIdentifier, userId.Value);
            SqlParameters.Add(command.Parameters, "@title", SqlDbType.NVarChar, title.Trim(), 200);
            SqlParameters.Add(command.Parameters, "@now", SqlDbType.DateTime2, SqlParameters.Utc(now));
            var rowVersion = (byte[]?)await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("STORE_CONVERSATION_CREATE_INCOMPLETE");
            return new ConversationSummary(id, userId, title.Trim(), ConversationStatus.Active, now, rowVersion);
        }
        catch (SqlException exception)
        {
            throw new SqlPersistenceException("STORE_CONVERSATION_CREATE_FAILED", exception);
        }
    }

    public async ValueTask<ConversationSnapshot?> GetAsync(
        ConversationId conversationId,
        UserId userId,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
            var summary = await ReadSummaryAsync(connection, conversationId, userId, cancellationToken).ConfigureAwait(false);
            if (summary is null)
            {
                return null;
            }

            await using var command = connection.CreateCommand();
            command.CommandTimeout = connectionFactory.Options.CommandTimeoutSeconds;
            command.CommandText = """
                SELECT m.Id, m.ConversationId, m.TurnId, m.Role, m.Content, m.SequenceNumber, m.CreatedAtUtc
                FROM dbo.FddAgentMessage AS m
                INNER JOIN dbo.FddAgentConversation AS c ON c.Id = m.ConversationId
                WHERE m.ConversationId = @conversationId AND c.UserId = @userId
                ORDER BY m.SequenceNumber ASC;
                """;
            SqlParameters.Add(command.Parameters, "@conversationId", SqlDbType.UniqueIdentifier, conversationId.Value);
            SqlParameters.Add(command.Parameters, "@userId", SqlDbType.UniqueIdentifier, userId.Value);
            var messages = new List<ConversationMessage>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                messages.Add(new ConversationMessage(
                    new MessageId(reader.GetGuid(0)),
                    new ConversationId(reader.GetGuid(1)),
                    reader.IsDBNull(2) ? null : new TurnId(reader.GetGuid(2)),
                    Enum.Parse<MessageRole>(reader.GetString(3), ignoreCase: false),
                    reader.GetString(4),
                    reader.GetInt64(5),
                    UtcOffset(reader.GetDateTime(6))));
            }

            return new ConversationSnapshot(summary, messages);
        }
        catch (SqlException exception)
        {
            throw new SqlPersistenceException("STORE_CONVERSATION_READ_FAILED", exception);
        }
    }

    public async ValueTask<IReadOnlyList<ConversationSummary>> ListAsync(
        UserId userId,
        ConversationStatus status,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandTimeout = connectionFactory.Options.CommandTimeoutSeconds;
            command.CommandText = """
                SELECT c.Id,
                       c.UserId,
                       COALESCE(
                           (SELECT TOP (1) m.Content
                            FROM dbo.FddAgentMessage AS m
                            WHERE m.ConversationId = c.Id AND m.Role = N'User'
                            ORDER BY m.SequenceNumber ASC),
                           c.Title) AS Title,
                       c.Status,
                       c.UpdatedAtUtc,
                       c.RowVersion
                FROM dbo.FddAgentConversation AS c
                WHERE c.UserId = @userId AND c.Status = @status
                ORDER BY c.UpdatedAtUtc DESC;
                """;
            SqlParameters.Add(command.Parameters, "@userId", SqlDbType.UniqueIdentifier, userId.Value);
            SqlParameters.Add(command.Parameters, "@status", SqlDbType.NVarChar, status.ToString(), 16);
            var values = new List<ConversationSummary>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                values.Add(ReadSummary(reader));
            }

            return values;
        }
        catch (SqlException exception)
        {
            throw new SqlPersistenceException("STORE_CONVERSATION_LIST_FAILED", exception);
        }
    }

    public async ValueTask<bool> ArchiveAsync(
        ConversationId conversationId,
        UserId userId,
        CancellationToken cancellationToken)
    {
        return await ChangeStatusAsync(
            conversationId,
            userId,
            ConversationStatus.Active,
            ConversationStatus.Archived,
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<bool> RestoreAsync(
        ConversationId conversationId,
        UserId userId,
        CancellationToken cancellationToken)
    {
        return await ChangeStatusAsync(
            conversationId,
            userId,
            ConversationStatus.Archived,
            ConversationStatus.Active,
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<bool> ChangeStatusAsync(
        ConversationId conversationId,
        UserId userId,
        ConversationStatus expectedStatus,
        ConversationStatus targetStatus,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandTimeout = connectionFactory.Options.CommandTimeoutSeconds;
            command.CommandText = """
                UPDATE dbo.FddAgentConversation
                SET Status = @targetStatus,
                    ArchivedAtUtc = CASE WHEN @targetStatus = N'Archived' THEN SYSUTCDATETIME() ELSE NULL END,
                    UpdatedAtUtc = SYSUTCDATETIME()
                WHERE Id = @conversationId AND UserId = @userId AND Status = @expectedStatus;
                """;
            SqlParameters.Add(command.Parameters, "@conversationId", SqlDbType.UniqueIdentifier, conversationId.Value);
            SqlParameters.Add(command.Parameters, "@userId", SqlDbType.UniqueIdentifier, userId.Value);
            SqlParameters.Add(command.Parameters, "@expectedStatus", SqlDbType.NVarChar, expectedStatus.ToString(), 16);
            SqlParameters.Add(command.Parameters, "@targetStatus", SqlDbType.NVarChar, targetStatus.ToString(), 16);
            return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
        }
        catch (SqlException exception)
        {
            var operation = targetStatus == ConversationStatus.Archived ? "ARCHIVE" : "RESTORE";
            throw new SqlPersistenceException($"STORE_CONVERSATION_{operation}_FAILED", exception);
        }
    }

    private async ValueTask<ConversationSummary?> ReadSummaryAsync(
        SqlConnection connection,
        ConversationId conversationId,
        UserId userId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandTimeout = connectionFactory.Options.CommandTimeoutSeconds;
        command.CommandText = """
            SELECT c.Id,
                   c.UserId,
                   COALESCE(
                       (SELECT TOP (1) m.Content
                        FROM dbo.FddAgentMessage AS m
                        WHERE m.ConversationId = c.Id AND m.Role = N'User'
                        ORDER BY m.SequenceNumber ASC),
                       c.Title) AS Title,
                   c.Status,
                   c.UpdatedAtUtc,
                   c.RowVersion
            FROM dbo.FddAgentConversation AS c
            WHERE c.Id = @conversationId AND c.UserId = @userId;
            """;
        SqlParameters.Add(command.Parameters, "@conversationId", SqlDbType.UniqueIdentifier, conversationId.Value);
        SqlParameters.Add(command.Parameters, "@userId", SqlDbType.UniqueIdentifier, userId.Value);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadSummary(reader) : null;
    }

    private static ConversationSummary ReadSummary(SqlDataReader reader) =>
        new(
            new ConversationId(reader.GetGuid(0)),
            new UserId(reader.GetGuid(1)),
            reader.GetString(2),
            Enum.Parse<ConversationStatus>(reader.GetString(3), ignoreCase: false),
            UtcOffset(reader.GetDateTime(4)),
            reader.GetFieldValue<byte[]>(5));

    private static DateTimeOffset UtcOffset(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
}
