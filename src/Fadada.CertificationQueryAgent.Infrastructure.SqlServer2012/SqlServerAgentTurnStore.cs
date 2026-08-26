// Persists a turn and its messages atomically, using row versions to reject concurrent writers.
using System.Data;
using Fadada.CertificationQueryAgent.Application.AgentTurns;
using Fadada.CertificationQueryAgent.Application.Conversations;
using Fadada.CertificationQueryAgent.Application.Persistence;
using Microsoft.Data.SqlClient;

namespace Fadada.CertificationQueryAgent.Infrastructure.SqlServer2012;

/// <summary>
/// 持久化 Agent 回合的开始与终态，并用并发约束阻止同会话重复执行。
/// </summary>
public sealed class SqlServerAgentTurnStore(SqlServerConnectionFactory connectionFactory) : IAgentTurnStore
{
    public async ValueTask<byte[]> StartAsync(AgentTurnStart turn, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(turn);
        ValidateRowVersion(turn.ExpectedConversationRowVersion);
        if (turn.UserMessage.Role != MessageRole.User ||
            turn.UserMessage.ConversationId != turn.ConversationId ||
            turn.UserMessage.TurnId != turn.TurnId)
        {
            throw new ArgumentException("Turn user message is inconsistent.", nameof(turn));
        }

        try
        {
            await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var rowVersion = await AdvanceConversationAsync(
                connection,
                transaction,
                turn.ConversationId.Value,
                turn.UserId.Value,
                turn.UserMessage.SequenceNumber,
                turn.ExpectedConversationRowVersion,
                turn.StartedAtUtc,
                turn.UserMessage.SequenceNumber == 1
                    ? ConversationTitle.FromFirstUserMessage(turn.UserMessage.Content)
                    : null,
                cancellationToken).ConfigureAwait(false);

            await using (var command = CreateCommand(connection, transaction))
            {
                command.CommandText = """
                    INSERT dbo.FddAgentTurn
                        (Id, ConversationId, TraceId, UserMessageId, PromptVersion, PromptSha256,
                         ModelProfile, ToolSetVersion, Status, StartedAtUtc)
                    VALUES
                        (@id, @conversationId, @traceId, @userMessageId, @promptVersion, @promptSha256,
                         @modelProfile, @toolSetVersion, N'Started', @startedAtUtc);
                    """;
                SqlParameters.Add(command.Parameters, "@id", SqlDbType.UniqueIdentifier, turn.TurnId.Value);
                SqlParameters.Add(command.Parameters, "@conversationId", SqlDbType.UniqueIdentifier, turn.ConversationId.Value);
                SqlParameters.Add(command.Parameters, "@traceId", SqlDbType.UniqueIdentifier, turn.TraceId);
                SqlParameters.Add(command.Parameters, "@userMessageId", SqlDbType.UniqueIdentifier, turn.UserMessage.Id.Value);
                SqlParameters.Add(command.Parameters, "@promptVersion", SqlDbType.NVarChar, turn.PromptVersion, 64);
                SqlParameters.Add(command.Parameters, "@promptSha256", SqlDbType.Char, turn.PromptSha256, 64);
                SqlParameters.Add(command.Parameters, "@modelProfile", SqlDbType.NVarChar, turn.ModelProfile, 128);
                SqlParameters.Add(command.Parameters, "@toolSetVersion", SqlDbType.NVarChar, turn.ToolSetVersion, 64);
                SqlParameters.Add(command.Parameters, "@startedAtUtc", SqlDbType.DateTime2, SqlParameters.Utc(turn.StartedAtUtc));
                await RequireOneAsync(command, cancellationToken, "STORE_TURN_START_INCOMPLETE").ConfigureAwait(false);
            }

            await InsertMessageAsync(connection, transaction, turn.UserMessage, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return rowVersion;
        }
        catch (PersistenceConcurrencyException)
        {
            throw;
        }
        catch (SqlException exception)
        {
            throw new SqlPersistenceException("STORE_TURN_START_FAILED", exception);
        }
    }

    public async ValueTask<byte[]> CompleteAsync(AgentTurnCompletion turn, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(turn);
        ValidateRowVersion(turn.ExpectedConversationRowVersion);
        if (turn.Status == AgentTurnStatus.Started ||
            turn.ModelCallCount is < 0 or > AgentExecutionLimits.MaximumModelCallsPerTurn ||
            turn.ToolCallCount is < 0 or > AgentExecutionLimits.MaximumDomainToolCallsPerTurn ||
            turn.InputTokens < 0 || turn.OutputTokens < 0 || turn.EstimatedCost < 0)
        {
            throw new ArgumentException("Turn metrics are outside configured budgets.", nameof(turn));
        }

        if (turn.AssistantMessage is not null &&
            (turn.AssistantMessage.Role != MessageRole.Assistant ||
             turn.AssistantMessage.ConversationId != turn.ConversationId ||
             turn.AssistantMessage.TurnId != turn.TurnId))
        {
            throw new ArgumentException("Turn assistant message is inconsistent.", nameof(turn));
        }

        try
        {
            await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var rowVersion = turn.AssistantMessage is null
                ? await TouchConversationAsync(connection, transaction, turn, cancellationToken).ConfigureAwait(false)
                : await AdvanceConversationAsync(
                    connection,
                    transaction,
                    turn.ConversationId.Value,
                    turn.UserId.Value,
                    turn.AssistantMessage.SequenceNumber,
                    turn.ExpectedConversationRowVersion,
                    turn.CompletedAtUtc,
                    null,
                    cancellationToken).ConfigureAwait(false);

            await using (var command = CreateCommand(connection, transaction))
            {
                command.CommandText = """
                    UPDATE dbo.FddAgentTurn
                    SET Status = @status,
                        ModelCallCount = @modelCallCount,
                        ToolCallCount = @toolCallCount,
                        InputTokens = @inputTokens,
                        OutputTokens = @outputTokens,
                        EstimatedCost = @estimatedCost,
                        CompletedAtUtc = @completedAtUtc,
                        SafeErrorCode = @safeErrorCode
                    WHERE Id = @id AND ConversationId = @conversationId AND Status = N'Started';
                    """;
                SqlParameters.Add(command.Parameters, "@status", SqlDbType.NVarChar, turn.Status.ToString(), 32);
                SqlParameters.Add(command.Parameters, "@modelCallCount", SqlDbType.Int, turn.ModelCallCount);
                SqlParameters.Add(command.Parameters, "@toolCallCount", SqlDbType.Int, turn.ToolCallCount);
                SqlParameters.Add(command.Parameters, "@inputTokens", SqlDbType.Int, turn.InputTokens);
                SqlParameters.Add(command.Parameters, "@outputTokens", SqlDbType.Int, turn.OutputTokens);
                SqlParameters.Add(command.Parameters, "@estimatedCost", SqlDbType.Decimal, turn.EstimatedCost);
                command.Parameters["@estimatedCost"].Precision = 19;
                command.Parameters["@estimatedCost"].Scale = 8;
                SqlParameters.Add(command.Parameters, "@completedAtUtc", SqlDbType.DateTime2, SqlParameters.Utc(turn.CompletedAtUtc));
                SqlParameters.Add(command.Parameters, "@safeErrorCode", SqlDbType.NVarChar, turn.SafeErrorCode, 64);
                SqlParameters.Add(command.Parameters, "@id", SqlDbType.UniqueIdentifier, turn.TurnId.Value);
                SqlParameters.Add(command.Parameters, "@conversationId", SqlDbType.UniqueIdentifier, turn.ConversationId.Value);
                await RequireOneAsync(command, cancellationToken, "STORE_TURN_COMPLETION_CONFLICT").ConfigureAwait(false);
            }

            if (turn.AssistantMessage is not null)
            {
                await InsertMessageAsync(connection, transaction, turn.AssistantMessage, cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return rowVersion;
        }
        catch (PersistenceConcurrencyException)
        {
            throw;
        }
        catch (SqlException exception)
        {
            throw new SqlPersistenceException("STORE_TURN_COMPLETION_FAILED", exception);
        }
    }

    private async ValueTask<byte[]> AdvanceConversationAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        Guid conversationId,
        Guid userId,
        long sequenceNumber,
        byte[] expectedRowVersion,
        DateTimeOffset updatedAtUtc,
        string? title,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction);
        command.CommandText = """
            UPDATE dbo.FddAgentConversation
            SET Title = COALESCE(@title, Title),
                NextSequenceNumber = NextSequenceNumber + 1,
                UpdatedAtUtc = @updatedAtUtc
            OUTPUT inserted.RowVersion
            WHERE Id = @conversationId AND UserId = @userId AND Status = N'Active'
              AND NextSequenceNumber = @sequenceNumber AND RowVersion = @rowVersion;
            """;
        SqlParameters.Add(command.Parameters, "@conversationId", SqlDbType.UniqueIdentifier, conversationId);
        SqlParameters.Add(command.Parameters, "@userId", SqlDbType.UniqueIdentifier, userId);
        SqlParameters.Add(command.Parameters, "@sequenceNumber", SqlDbType.BigInt, sequenceNumber);
        SqlParameters.Add(command.Parameters, "@rowVersion", SqlDbType.Binary, expectedRowVersion, 8);
        SqlParameters.Add(command.Parameters, "@updatedAtUtc", SqlDbType.DateTime2, SqlParameters.Utc(updatedAtUtc));
        SqlParameters.Add(command.Parameters, "@title", SqlDbType.NVarChar, title, ConversationTitle.MaximumLength);
        return (byte[]?)await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new PersistenceConcurrencyException("STORE_CONVERSATION_CONFLICT");
    }

    private async ValueTask<byte[]> TouchConversationAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        AgentTurnCompletion turn,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction);
        command.CommandText = """
            UPDATE dbo.FddAgentConversation
            SET UpdatedAtUtc = @updatedAtUtc
            OUTPUT inserted.RowVersion
            WHERE Id = @conversationId AND UserId = @userId AND Status = N'Active' AND RowVersion = @rowVersion;
            """;
        SqlParameters.Add(command.Parameters, "@conversationId", SqlDbType.UniqueIdentifier, turn.ConversationId.Value);
        SqlParameters.Add(command.Parameters, "@userId", SqlDbType.UniqueIdentifier, turn.UserId.Value);
        SqlParameters.Add(command.Parameters, "@rowVersion", SqlDbType.Binary, turn.ExpectedConversationRowVersion, 8);
        SqlParameters.Add(command.Parameters, "@updatedAtUtc", SqlDbType.DateTime2, SqlParameters.Utc(turn.CompletedAtUtc));
        return (byte[]?)await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new PersistenceConcurrencyException("STORE_CONVERSATION_CONFLICT");
    }

    private async ValueTask InsertMessageAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        ConversationMessage message,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction);
        command.CommandText = """
            INSERT dbo.FddAgentMessage (Id, ConversationId, TurnId, Role, Content, SequenceNumber, CreatedAtUtc)
            VALUES (@id, @conversationId, @turnId, @role, @content, @sequenceNumber, @createdAtUtc);
            """;
        SqlParameters.Add(command.Parameters, "@id", SqlDbType.UniqueIdentifier, message.Id.Value);
        SqlParameters.Add(command.Parameters, "@conversationId", SqlDbType.UniqueIdentifier, message.ConversationId.Value);
        SqlParameters.Add(command.Parameters, "@turnId", SqlDbType.UniqueIdentifier, message.TurnId?.Value);
        SqlParameters.Add(command.Parameters, "@role", SqlDbType.NVarChar, message.Role.ToString(), 16);
        SqlParameters.Add(command.Parameters, "@content", SqlDbType.NVarChar, message.Content, -1);
        SqlParameters.Add(command.Parameters, "@sequenceNumber", SqlDbType.BigInt, message.SequenceNumber);
        SqlParameters.Add(command.Parameters, "@createdAtUtc", SqlDbType.DateTime2, SqlParameters.Utc(message.CreatedAtUtc));
        await RequireOneAsync(command, cancellationToken, "STORE_MESSAGE_INSERT_INCOMPLETE").ConfigureAwait(false);
    }

    private SqlCommand CreateCommand(SqlConnection connection, SqlTransaction transaction) =>
        new()
        {
            Connection = connection,
            Transaction = transaction,
            CommandTimeout = connectionFactory.Options.CommandTimeoutSeconds
        };

    private static async ValueTask RequireOneAsync(
        SqlCommand command,
        CancellationToken cancellationToken,
        string errorCode)
    {
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new PersistenceConcurrencyException(errorCode);
        }
    }

    private static void ValidateRowVersion(byte[] value)
    {
        if (value is not { Length: 8 })
        {
            throw new ArgumentException("Expected row version must contain eight bytes.", nameof(value));
        }
    }
}
