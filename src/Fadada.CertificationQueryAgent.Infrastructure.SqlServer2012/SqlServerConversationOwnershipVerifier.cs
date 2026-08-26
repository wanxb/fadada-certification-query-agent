// Performs ownership checks in SQL using both conversation and authenticated user identifiers.
using System.Data;
using Fadada.CertificationQueryAgent.Application.Common;
using Fadada.CertificationQueryAgent.Application.DomainTools;
using Microsoft.Data.SqlClient;

namespace Fadada.CertificationQueryAgent.Infrastructure.SqlServer2012;

/// <summary>
/// 在工具执行前查询会话所有权，将身份校验固定在持久化边界。
/// </summary>
public sealed class SqlServerConversationOwnershipVerifier(
    SqlServerConnectionFactory connectionFactory) : IConversationOwnershipVerifier
{
    public async ValueTask<bool> IsOwnerAsync(
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
                SELECT TOP (1) CAST(1 AS BIT)
                FROM dbo.FddAgentConversation
                WHERE Id = @conversationId AND UserId = @userId AND Status = N'Active';
                """;
            SqlParameters.Add(command.Parameters, "@conversationId", SqlDbType.UniqueIdentifier, conversationId.Value);
            SqlParameters.Add(command.Parameters, "@userId", SqlDbType.UniqueIdentifier, userId.Value);
            return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
        }
        catch (SqlException exception)
        {
            throw new SqlPersistenceException("STORE_OWNERSHIP_READ_FAILED", exception);
        }
    }
}
