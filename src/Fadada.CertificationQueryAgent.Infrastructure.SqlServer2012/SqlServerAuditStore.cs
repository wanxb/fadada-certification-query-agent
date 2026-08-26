// Implements fail-closed audit prewrites and terminal updates with parameterized SQL only.
using System.Data;
using Fadada.CertificationQueryAgent.Application.AgentTurns;
using Fadada.CertificationQueryAgent.Application.Auditing;
using Fadada.CertificationQueryAgent.Application.DomainTools;
using Microsoft.Data.SqlClient;

namespace Fadada.CertificationQueryAgent.Infrastructure.SqlServer2012;

/// <summary>
/// 以先写后调方式记录领域工具和外部接口审计，审计失败时阻断真实调用。
/// </summary>
public sealed class SqlServerAuditStore(SqlServerConnectionFactory connectionFactory) : IAuditStore
{
    public async ValueTask PrewriteAsync(AuditPrewrite entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        try
        {
            if (entry.Operation.StartsWith("Tool:", StringComparison.Ordinal))
            {
                await PrewriteToolAsync(entry, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (entry.Operation.StartsWith("Fadada:", StringComparison.Ordinal))
            {
                await PrewriteExternalAsync(entry, cancellationToken).ConfigureAwait(false);
                return;
            }

            throw new InvalidOperationException("AUDIT_OPERATION_NOT_SUPPORTED");
        }
        catch (SqlException exception)
        {
            throw new SqlPersistenceException("AUDIT_PREWRITE_FAILED", exception);
        }
    }

    public async ValueTask CompleteAsync(AuditCompletion completion, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(completion);
        try
        {
            await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandTimeout = connectionFactory.Options.CommandTimeoutSeconds;
            command.CommandText = completion.Kind switch
            {
                AuditOperationKind.Tool => """
                    UPDATE dbo.FddAgentToolCall
                    SET Status = @status, DurationMilliseconds = @duration, CompletedAtUtc = @completedAtUtc,
                        SafeErrorCode = @safeErrorCode, SafeResultSummary = @safeResultSummary
                    WHERE Id = @id AND Status = N'Started';
                    """,
                AuditOperationKind.External => """
                    UPDATE dbo.FddAgentExternalCall
                    SET Status = @status, DurationMilliseconds = @duration, CompletedAtUtc = @completedAtUtc, SafeErrorCode = @safeErrorCode
                    WHERE Id = @id AND Status = N'Started';
                    """,
                _ => throw new ArgumentOutOfRangeException(nameof(completion))
            };
            AddCompletionParameters(command, completion);
            var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (affected != 1)
            {
                throw new InvalidOperationException("AUDIT_COMPLETION_NOT_FOUND");
            }
        }
        catch (SqlException exception)
        {
            throw new SqlPersistenceException("AUDIT_COMPLETION_FAILED", exception);
        }
    }

    private async ValueTask PrewriteToolAsync(AuditPrewrite entry, CancellationToken cancellationToken)
    {
        var toolName = entry.Operation["Tool:".Length..];
        if (!DomainToolRegistry.TryGet(toolName, out _))
        {
            throw new InvalidOperationException("AUDIT_TOOL_NOT_REGISTERED");
        }

        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await connection
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = connectionFactory.Options.CommandTimeoutSeconds;
        command.CommandText = """
            DECLARE @sequenceNumber INT;

            SELECT @sequenceNumber = ISNULL(MAX(tc.SequenceNumber), 0) + 1
            FROM dbo.FddAgentToolCall AS tc WITH (UPDLOCK, HOLDLOCK)
            WHERE tc.TurnId = @turnId;

            IF @sequenceNumber > @maximumToolCalls
                RAISERROR (N'AUDIT_TOOL_SEQUENCE_EXCEEDED', 16, 1);

            INSERT dbo.FddAgentToolCall
                (Id, TurnId, SequenceNumber, ToolName, PolicyDecision, PolicyErrorCode,
                 SafeArgumentsSummary, Status, StartedAtUtc)
            SELECT @id, t.Id, @sequenceNumber, @toolName, N'Allowed', NULL, @safeArgumentsSummary, N'Started', @startedAtUtc
            FROM dbo.FddAgentTurn AS t
            INNER JOIN dbo.FddAgentConversation AS c ON c.Id = t.ConversationId
            WHERE t.Id = @turnId AND t.ConversationId = @conversationId
              AND c.UserId = @userId AND t.Status = N'Started';
            """;
        SqlParameters.Add(command.Parameters, "@id", SqlDbType.UniqueIdentifier, entry.AuditId);
        SqlParameters.Add(command.Parameters, "@turnId", SqlDbType.UniqueIdentifier, entry.TurnId.Value);
        SqlParameters.Add(command.Parameters, "@conversationId", SqlDbType.UniqueIdentifier, entry.ConversationId.Value);
        SqlParameters.Add(command.Parameters, "@userId", SqlDbType.UniqueIdentifier, entry.UserId.Value);
        SqlParameters.Add(command.Parameters, "@toolName", SqlDbType.NVarChar, toolName, 64);
        SqlParameters.Add(command.Parameters, "@maximumToolCalls", SqlDbType.Int, AgentExecutionLimits.MaximumDomainToolCallsPerTurn);
        SqlParameters.Add(command.Parameters, "@safeArgumentsSummary", SqlDbType.NVarChar, entry.SafeArgumentsSummary, 1000);
        SqlParameters.Add(command.Parameters, "@startedAtUtc", SqlDbType.DateTime2, SqlParameters.Utc(entry.StartedAtUtc));
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException("AUDIT_TOOL_PREWRITE_INCOMPLETE");
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask PrewriteExternalAsync(AuditPrewrite entry, CancellationToken cancellationToken)
    {
        var parts = entry.Operation.Split(':', StringSplitOptions.None);
        if (parts.Length != 3 || parts[1].Length is < 1 or > 64 || parts[2] is not ("GET" or "POST"))
        {
            throw new InvalidOperationException("AUDIT_EXTERNAL_OPERATION_INVALID");
        }

        if (entry.ParentToolCallId is not { } parentToolCallId || parentToolCallId.Value == Guid.Empty)
        {
            throw new InvalidOperationException("AUDIT_PARENT_TOOL_REQUIRED");
        }

        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await connection
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = connectionFactory.Options.CommandTimeoutSeconds;
        command.CommandText = """
            DECLARE @toolCallId UNIQUEIDENTIFIER;
            DECLARE @sequenceNumber INT;

            SELECT @toolCallId = tc.Id
            FROM dbo.FddAgentToolCall AS tc WITH (UPDLOCK, HOLDLOCK)
            INNER JOIN dbo.FddAgentTurn AS t ON t.Id = tc.TurnId
            INNER JOIN dbo.FddAgentConversation AS c ON c.Id = t.ConversationId
            WHERE tc.Id = @parentToolCallId AND tc.TurnId = @turnId
              AND t.ConversationId = @conversationId AND c.UserId = @userId;

            IF @toolCallId IS NULL
                RAISERROR (N'AUDIT_PARENT_TOOL_NOT_FOUND', 16, 1);

            SELECT @sequenceNumber = ISNULL(MAX(SequenceNumber), 0) + 1
            FROM dbo.FddAgentExternalCall WITH (UPDLOCK, HOLDLOCK)
            WHERE ToolCallId = @toolCallId;

            INSERT dbo.FddAgentExternalCall
                (Id, ToolCallId, SequenceNumber, EndpointKey, HttpMethod, Status, StartedAtUtc)
            VALUES
                (@id, @toolCallId, @sequenceNumber, @endpointKey, @httpMethod, N'Started', @startedAtUtc);
            """;
        SqlParameters.Add(command.Parameters, "@id", SqlDbType.UniqueIdentifier, entry.AuditId);
        SqlParameters.Add(command.Parameters, "@turnId", SqlDbType.UniqueIdentifier, entry.TurnId.Value);
        SqlParameters.Add(command.Parameters, "@conversationId", SqlDbType.UniqueIdentifier, entry.ConversationId.Value);
        SqlParameters.Add(command.Parameters, "@userId", SqlDbType.UniqueIdentifier, entry.UserId.Value);
        SqlParameters.Add(command.Parameters, "@parentToolCallId", SqlDbType.UniqueIdentifier, parentToolCallId.Value);
        SqlParameters.Add(command.Parameters, "@endpointKey", SqlDbType.NVarChar, parts[1], 64);
        SqlParameters.Add(command.Parameters, "@httpMethod", SqlDbType.NVarChar, parts[2], 8);
        SqlParameters.Add(command.Parameters, "@startedAtUtc", SqlDbType.DateTime2, SqlParameters.Utc(entry.StartedAtUtc));
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException("AUDIT_EXTERNAL_PREWRITE_INCOMPLETE");
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void AddCompletionParameters(SqlCommand command, AuditCompletion completion)
    {
        SqlParameters.Add(command.Parameters, "@id", SqlDbType.UniqueIdentifier, completion.AuditId);
        SqlParameters.Add(command.Parameters, "@status", SqlDbType.NVarChar, completion.Status.ToString(), 32);
        SqlParameters.Add(command.Parameters, "@duration", SqlDbType.BigInt, completion.DurationMilliseconds);
        SqlParameters.Add(command.Parameters, "@completedAtUtc", SqlDbType.DateTime2, SqlParameters.Utc(completion.CompletedAtUtc));
        SqlParameters.Add(command.Parameters, "@safeErrorCode", SqlDbType.NVarChar, completion.SafeErrorCode, 64);
        SqlParameters.Add(command.Parameters, "@safeResultSummary", SqlDbType.NVarChar, completion.SafeResultSummary, 1000);
    }
}

/// <summary>
/// 记录模型调用的版本、用量、成本和安全终态，不保存秘密配置。
/// </summary>
public sealed class SqlServerModelCallAuditStore(
    SqlServerConnectionFactory connectionFactory) : IModelCallAuditStore
{
    public async ValueTask PrewriteAsync(ModelCallAuditStart entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Id == Guid.Empty || entry.AttemptNumber is < 1 or > AgentExecutionLimits.MaximumModelCallsPerTurn ||
            string.IsNullOrWhiteSpace(entry.Provider) || entry.Provider.Length > 64 ||
            string.IsNullOrWhiteSpace(entry.ModelName) || entry.ModelName.Length > 128)
        {
            throw new ArgumentException("Model call audit is invalid.", nameof(entry));
        }

        try
        {
            await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandTimeout = connectionFactory.Options.CommandTimeoutSeconds;
            command.CommandText = """
                INSERT dbo.FddAgentModelCall
                    (Id, TurnId, AttemptNumber, Provider, ModelName, Status, StartedAtUtc)
                SELECT @id, t.Id, @attemptNumber, @provider, @modelName, N'Started', @startedAtUtc
                FROM dbo.FddAgentTurn AS t
                INNER JOIN dbo.FddAgentConversation AS c ON c.Id = t.ConversationId
                WHERE t.Id = @turnId AND t.ConversationId = @conversationId
                  AND c.UserId = @userId AND t.Status = N'Started';
                """;
            SqlParameters.Add(command.Parameters, "@id", SqlDbType.UniqueIdentifier, entry.Id);
            SqlParameters.Add(command.Parameters, "@turnId", SqlDbType.UniqueIdentifier, entry.TurnId.Value);
            SqlParameters.Add(command.Parameters, "@conversationId", SqlDbType.UniqueIdentifier, entry.ConversationId.Value);
            SqlParameters.Add(command.Parameters, "@userId", SqlDbType.UniqueIdentifier, entry.UserId.Value);
            SqlParameters.Add(command.Parameters, "@attemptNumber", SqlDbType.Int, entry.AttemptNumber);
            SqlParameters.Add(command.Parameters, "@provider", SqlDbType.NVarChar, entry.Provider, 64);
            SqlParameters.Add(command.Parameters, "@modelName", SqlDbType.NVarChar, entry.ModelName, 128);
            SqlParameters.Add(command.Parameters, "@startedAtUtc", SqlDbType.DateTime2, SqlParameters.Utc(entry.StartedAtUtc));
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new InvalidOperationException("AUDIT_MODEL_PREWRITE_INCOMPLETE");
            }
        }
        catch (SqlException exception)
        {
            throw new SqlPersistenceException("AUDIT_MODEL_PREWRITE_FAILED", exception);
        }
    }

    public async ValueTask CompleteAsync(ModelCallAuditCompletion completion, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(completion);
        if (completion.Status == AuditOperationStatus.Started || completion.InputTokens < 0 ||
            completion.OutputTokens < 0 || completion.EstimatedCost < 0 || completion.DurationMilliseconds < 0)
        {
            throw new ArgumentException("Model call completion is invalid.", nameof(completion));
        }

        try
        {
            await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandTimeout = connectionFactory.Options.CommandTimeoutSeconds;
            command.CommandText = """
                UPDATE dbo.FddAgentModelCall
                SET Status = @status,
                    InputTokens = @inputTokens,
                    OutputTokens = @outputTokens,
                    EstimatedCost = @estimatedCost,
                    DurationMilliseconds = @duration,
                    CompletedAtUtc = @completedAtUtc,
                    SafeErrorCode = @safeErrorCode
                WHERE Id = @id AND TurnId = @turnId AND Status = N'Started';
                """;
            SqlParameters.Add(command.Parameters, "@id", SqlDbType.UniqueIdentifier, completion.Id);
            SqlParameters.Add(command.Parameters, "@turnId", SqlDbType.UniqueIdentifier, completion.TurnId.Value);
            SqlParameters.Add(command.Parameters, "@status", SqlDbType.NVarChar, completion.Status.ToString(), 32);
            SqlParameters.Add(command.Parameters, "@inputTokens", SqlDbType.Int, completion.InputTokens);
            SqlParameters.Add(command.Parameters, "@outputTokens", SqlDbType.Int, completion.OutputTokens);
            var cost = SqlParameters.Add(command.Parameters, "@estimatedCost", SqlDbType.Decimal, completion.EstimatedCost);
            cost.Precision = 19;
            cost.Scale = 8;
            SqlParameters.Add(command.Parameters, "@duration", SqlDbType.BigInt, completion.DurationMilliseconds);
            SqlParameters.Add(command.Parameters, "@completedAtUtc", SqlDbType.DateTime2, SqlParameters.Utc(completion.CompletedAtUtc));
            SqlParameters.Add(command.Parameters, "@safeErrorCode", SqlDbType.NVarChar, completion.SafeErrorCode, 64);
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new InvalidOperationException("AUDIT_MODEL_COMPLETION_NOT_FOUND");
            }
        }
        catch (SqlException exception)
        {
            throw new SqlPersistenceException("AUDIT_MODEL_COMPLETION_FAILED", exception);
        }
    }
}
