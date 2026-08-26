// Keeps SQL Server 2012 live checks behind explicit environment gates and offline by default.
using Fadada.CertificationQueryAgent.Application.Authentication;
using Fadada.CertificationQueryAgent.Application.Auditing;
using Fadada.CertificationQueryAgent.Application.Common;
using Fadada.CertificationQueryAgent.Application.Conversations;
using Fadada.CertificationQueryAgent.Application.Persistence;
using Fadada.CertificationQueryAgent.Infrastructure.SqlServer2012;

namespace Fadada.CertificationQueryAgent.IntegrationTests;

/// <summary>
/// 验证 SqlServer2012PersistenceTests 所覆盖的行为、边界条件和安全回归约束。
/// </summary>
public sealed class SqlServer2012PersistenceTests
{
    [Fact]
    public void Profiles_reject_wrong_targets_and_unsafe_production_transport()
    {
        var wrongLab = new SqlServer2012Options(
            "Server=not-approved;Database=FadadaAgentLab;User ID=fixture;Password=fixture;Encrypt=False",
            SqlPersistenceProfile.LabSqlServer2012);
        var unsafeProduction = new SqlServer2012Options(
            "Server=production.invalid;Database=FddAgent;User ID=fixture;Password=fixture;Encrypt=False;TrustServerCertificate=True",
            SqlPersistenceProfile.ProductionReference);

        Assert.Equal("STORE_LAB_TARGET_REJECTED", Assert.Throws<InvalidOperationException>(wrongLab.Validate).Message);
        Assert.Equal("STORE_PRODUCTION_TRANSPORT_REJECTED", Assert.Throws<InvalidOperationException>(unsafeProduction.Validate).Message);
    }

    [Fact]
    public void Options_never_render_connection_credentials()
    {
        var options = new SqlServer2012Options(
            "Server=localhost;Database=FadadaAgentLab;User ID=fixture-user;Password=fixture-secret;Encrypt=False",
            SqlPersistenceProfile.LabSqlServer2012);

        _ = options.Validate();

        Assert.DoesNotContain("fixture-user", options.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("fixture-secret", options.ToString(), StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", options.ToString(), StringComparison.Ordinal);
        Assert.False(options.Validate().PersistSecurityInfo);
    }

    [Fact]
    public async Task Turn_completion_rejects_non_terminal_status_before_opening_a_connection()
    {
        var factory = new SqlServerConnectionFactory(new SqlServer2012Options(
            "Server=localhost;Database=FadadaAgentLab;User ID=fixture;Password=fixture;Encrypt=False",
            SqlPersistenceProfile.LabSqlServer2012));
        var turn = new AgentTurnCompletion(
            TurnId.New(),
            ConversationId.New(),
            UserId.New(),
            AgentTurnStatus.Started,
            null,
            0,
            0,
            0,
            0,
            0,
            null,
            new byte[8],
            DateTimeOffset.UtcNow);

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await new SqlServerAgentTurnStore(factory).CompleteAsync(turn, CancellationToken.None));
    }

    [Fact]
    public async Task Live_lab_readiness_probe_is_explicit_and_read_only()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("FDD_RUN_SQL2012_READINESS_TESTS"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        var connectionString = Environment.GetEnvironmentVariable("FDD_TEST_SQLSERVER");
        Assert.False(string.IsNullOrWhiteSpace(connectionString));
        var factory = new SqlServerConnectionFactory(new SqlServer2012Options(
            connectionString!,
            SqlPersistenceProfile.LabSqlServer2012));

        var readiness = await factory.CheckReadinessAsync(CancellationToken.None);

        Assert.Equal(11, readiness.ServerMajorVersion);
        Assert.Equal(SqlServer2012Options.ApprovedLabDatabase, readiness.DatabaseName);
        Assert.Equal(110, readiness.CompatibilityLevel);
        Assert.True(readiness.IsReady, readiness.ErrorCode);
        Assert.Equal(2, readiness.SchemaVersion);
    }

    [Fact]
    public async Task Live_lab_round_trip_is_explicit_insert_only_and_owner_scoped()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("FDD_RUN_SQL2012_REPOSITORY_TESTS"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        var connectionString = Environment.GetEnvironmentVariable("FDD_TEST_SQLSERVER");
        Assert.False(string.IsNullOrWhiteSpace(connectionString));
        var factory = new SqlServerConnectionFactory(new SqlServer2012Options(
            connectionString!,
            SqlPersistenceProfile.LabSqlServer2012));
        var readiness = await factory.CheckReadinessAsync(CancellationToken.None);
        Assert.True(readiness.IsReady, readiness.ErrorCode);

        var runId = Guid.NewGuid().ToString("N");
        var userId = UserId.New();
        var userStore = new SqlServerUserStore(factory);
        await userStore.CreateAsync(new UserAccount(
            userId,
            $"eval-{runId}",
            $"EVAL-{runId.ToUpperInvariant()}",
            "Synthetic persistence probe",
            "synthetic-password-hash",
            Guid.NewGuid().ToString("N"),
            true,
            0,
            null,
            null,
            []),
            new AccountMutationAudit(Guid.NewGuid(), "AccountCreated", "integration-test", DateTimeOffset.UtcNow),
            CancellationToken.None);
        var user = await userStore.GetByIdAsync(userId, CancellationToken.None);
        Assert.NotNull(user);

        var conversations = new SqlServerConversationStore(factory);
        var conversation = await conversations.CreateAsync(userId, $"probe-{runId}", CancellationToken.None);
        Assert.NotNull(conversation.RowVersion);
        Assert.NotNull(await conversations.GetAsync(conversation.Id, userId, CancellationToken.None));
        Assert.Null(await conversations.GetAsync(conversation.Id, UserId.New(), CancellationToken.None));
        Assert.Single(await conversations.ListAsync(userId, ConversationStatus.Active, CancellationToken.None));
        Assert.Empty(await conversations.ListAsync(userId, ConversationStatus.Archived, CancellationToken.None));
        Assert.True(await conversations.ArchiveAsync(conversation.Id, userId, CancellationToken.None));
        Assert.Empty(await conversations.ListAsync(userId, ConversationStatus.Active, CancellationToken.None));
        Assert.Single(await conversations.ListAsync(userId, ConversationStatus.Archived, CancellationToken.None));
        Assert.True(await conversations.RestoreAsync(conversation.Id, userId, CancellationToken.None));
        conversation = (await conversations.GetAsync(conversation.Id, userId, CancellationToken.None))!.Conversation;

        var turnId = TurnId.New();
        var userMessage = new ConversationMessage(
            MessageId.New(),
            conversation.Id,
            turnId,
            MessageRole.User,
            "synthetic persistence probe",
            1,
            DateTimeOffset.UtcNow);
        var turns = new SqlServerAgentTurnStore(factory);
        var afterStart = await turns.StartAsync(new AgentTurnStart(
            turnId,
            conversation.Id,
            userId,
            Guid.NewGuid(),
            userMessage,
            "query-agent.v2",
            new string('0', 64),
            "offline-probe",
            "domain-tools.v1",
            conversation.RowVersion!,
            DateTimeOffset.UtcNow), CancellationToken.None);

        var toolCallId = ToolCallId.New();
        var audit = new SqlServerAuditStore(factory);
        await audit.PrewriteAsync(new AuditPrewrite(
            toolCallId.Value,
            userId,
            conversation.Id,
            turnId,
            "Tool:query_person",
            DateTimeOffset.UtcNow), CancellationToken.None);
        await audit.CompleteAsync(new AuditCompletion(
            toolCallId.Value,
            AuditOperationKind.Tool,
            AuditOperationStatus.Succeeded,
            null,
            1,
            DateTimeOffset.UtcNow), CancellationToken.None);

        var externalAuditId = Guid.NewGuid();
        await audit.PrewriteAsync(new AuditPrewrite(
            externalAuditId,
            userId,
            conversation.Id,
            turnId,
            "Fadada:GetAccount:GET",
            DateTimeOffset.UtcNow,
            ParentToolCallId: toolCallId), CancellationToken.None);
        await audit.CompleteAsync(new AuditCompletion(
            externalAuditId,
            AuditOperationKind.External,
            AuditOperationStatus.Succeeded,
            null,
            1,
            DateTimeOffset.UtcNow), CancellationToken.None);

        var secondToolCallId = ToolCallId.New();
        await audit.PrewriteAsync(new AuditPrewrite(
            secondToolCallId.Value,
            userId,
            conversation.Id,
            turnId,
            "Tool:query_company",
            DateTimeOffset.UtcNow), CancellationToken.None);
        await audit.CompleteAsync(new AuditCompletion(
            secondToolCallId.Value,
            AuditOperationKind.Tool,
            AuditOperationStatus.Succeeded,
            null,
            1,
            DateTimeOffset.UtcNow), CancellationToken.None);

        var secondExternalAuditId = Guid.NewGuid();
        await audit.PrewriteAsync(new AuditPrewrite(
            secondExternalAuditId,
            userId,
            conversation.Id,
            turnId,
            "Fadada:GetCompany:GET",
            DateTimeOffset.UtcNow,
            ParentToolCallId: secondToolCallId), CancellationToken.None);
        await audit.CompleteAsync(new AuditCompletion(
            secondExternalAuditId,
            AuditOperationKind.External,
            AuditOperationStatus.Succeeded,
            null,
            1,
            DateTimeOffset.UtcNow), CancellationToken.None);

        var assistantMessage = new ConversationMessage(
            MessageId.New(),
            conversation.Id,
            turnId,
            MessageRole.Assistant,
            "synthetic result",
            2,
            DateTimeOffset.UtcNow);
        var afterCompletion = await turns.CompleteAsync(new AgentTurnCompletion(
            turnId,
            conversation.Id,
            userId,
            AgentTurnStatus.Succeeded,
            assistantMessage,
            2,
            2,
            24,
            8,
            0.0001m,
            null,
            afterStart,
            DateTimeOffset.UtcNow), CancellationToken.None);
        Assert.Equal(8, afterCompletion.Length);

        var sessions = new SqlServerSessionStateStore(factory);
        await sessions.SaveAsync(new SessionState(
            conversation.Id,
            "maf",
            "1",
            [1, 2, 3],
            []), userId, CancellationToken.None);
        Assert.NotNull(await sessions.GetAsync(conversation.Id, userId, CancellationToken.None));
        Assert.Null(await sessions.GetAsync(conversation.Id, UserId.New(), CancellationToken.None));

        var diagnostics = new SqlServerDiagnosticPayloadStore(factory);
        var payloadId = Guid.NewGuid();
        await diagnostics.SaveAsync(new DiagnosticPayload(
            payloadId,
            userId,
            "Turn",
            turnId.Value,
            [4, 5, 6],
            DateTimeOffset.UtcNow.AddMinutes(5)), CancellationToken.None);
        Assert.NotNull(await diagnostics.GetAsync(payloadId, userId, CancellationToken.None));
        Assert.Null(await diagnostics.GetAsync(payloadId, UserId.New(), CancellationToken.None));
    }

}
