/* SQL Server 2012 compatible V2 indexes. Review and execute manually after 001-create-schema.sql. */
SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRY
    BEGIN TRANSACTION;

    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.FddAgentUser') AND name = N'UX_FddAgentUser_NormalizedUserName')
        CREATE UNIQUE NONCLUSTERED INDEX UX_FddAgentUser_NormalizedUserName
            ON dbo.FddAgentUser (NormalizedUserName);

    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.FddAgentUser') AND name = N'IX_FddAgentUser_ActiveLockout')
        CREATE NONCLUSTERED INDEX IX_FddAgentUser_ActiveLockout
            ON dbo.FddAgentUser (IsActive, LockoutEndUtc)
            INCLUDE (NormalizedUserName, SecurityStamp, AccessFailedCount);

    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.FddAgentConversation') AND name = N'IX_FddAgentConversation_UserStatusUpdated')
        CREATE NONCLUSTERED INDEX IX_FddAgentConversation_UserStatusUpdated
            ON dbo.FddAgentConversation (UserId, Status, UpdatedAtUtc DESC)
            INCLUDE (Title, ArchivedAtUtc);

    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.FddAgentSecurityEvent') AND name = N'IX_FddAgentSecurityEvent_UserOccurred')
        CREATE NONCLUSTERED INDEX IX_FddAgentSecurityEvent_UserOccurred
            ON dbo.FddAgentSecurityEvent (TargetUserId, OccurredAtUtc DESC)
            INCLUDE (EventType, Actor);

    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.FddAgentConversation') AND name = N'IX_FddAgentConversation_ArchivedExpiry')
        CREATE NONCLUSTERED INDEX IX_FddAgentConversation_ArchivedExpiry
            ON dbo.FddAgentConversation (Status, ArchivedAtUtc)
            INCLUDE (UserId, UpdatedAtUtc);

    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.FddAgentMessage') AND name = N'UX_FddAgentMessage_ConversationSequence')
        CREATE UNIQUE NONCLUSTERED INDEX UX_FddAgentMessage_ConversationSequence
            ON dbo.FddAgentMessage (ConversationId, SequenceNumber);

    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.FddAgentMessage') AND name = N'IX_FddAgentMessage_Turn')
        CREATE NONCLUSTERED INDEX IX_FddAgentMessage_Turn
            ON dbo.FddAgentMessage (TurnId)
            INCLUDE (ConversationId, Role, SequenceNumber, CreatedAtUtc);

    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.FddAgentTurn') AND name = N'UX_FddAgentTurn_TraceId')
        CREATE UNIQUE NONCLUSTERED INDEX UX_FddAgentTurn_TraceId
            ON dbo.FddAgentTurn (TraceId);

    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.FddAgentTurn') AND name = N'UX_FddAgentTurn_ConversationUserMessage')
        CREATE UNIQUE NONCLUSTERED INDEX UX_FddAgentTurn_ConversationUserMessage
            ON dbo.FddAgentTurn (ConversationId, UserMessageId);

    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.FddAgentTurn') AND name = N'IX_FddAgentTurn_ConversationStarted')
        CREATE NONCLUSTERED INDEX IX_FddAgentTurn_ConversationStarted
            ON dbo.FddAgentTurn (ConversationId, StartedAtUtc DESC)
            INCLUDE (Status, CompletedAtUtc, SafeErrorCode);

    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.FddAgentModelCall') AND name = N'UX_FddAgentModelCall_TurnAttempt')
        CREATE UNIQUE NONCLUSTERED INDEX UX_FddAgentModelCall_TurnAttempt
            ON dbo.FddAgentModelCall (TurnId, AttemptNumber);

    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.FddAgentModelCall') AND name = N'IX_FddAgentModelCall_StatusStarted')
        CREATE NONCLUSTERED INDEX IX_FddAgentModelCall_StatusStarted
            ON dbo.FddAgentModelCall (Status, StartedAtUtc)
            INCLUDE (TurnId, Provider, ModelName, SafeErrorCode);

    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.FddAgentToolCall') AND name = N'UX_FddAgentToolCall_TurnSequence')
        CREATE UNIQUE NONCLUSTERED INDEX UX_FddAgentToolCall_TurnSequence
            ON dbo.FddAgentToolCall (TurnId, SequenceNumber);

    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.FddAgentToolCall') AND name = N'IX_FddAgentToolCall_StatusStarted')
        CREATE NONCLUSTERED INDEX IX_FddAgentToolCall_StatusStarted
            ON dbo.FddAgentToolCall (Status, StartedAtUtc)
            INCLUDE (TurnId, ToolName, PolicyDecision, SafeErrorCode);

    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.FddAgentExternalCall') AND name = N'UX_FddAgentExternalCall_ToolSequence')
        CREATE UNIQUE NONCLUSTERED INDEX UX_FddAgentExternalCall_ToolSequence
            ON dbo.FddAgentExternalCall (ToolCallId, SequenceNumber);

    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.FddAgentExternalCall') AND name = N'IX_FddAgentExternalCall_EndpointStarted')
        CREATE NONCLUSTERED INDEX IX_FddAgentExternalCall_EndpointStarted
            ON dbo.FddAgentExternalCall (EndpointKey, StartedAtUtc)
            INCLUDE (ToolCallId, HttpMethod, Status, HttpStatusCode, SafeErrorCode);

    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.FddAgentDiagnosticPayload') AND name = N'IX_FddAgentDiagnosticPayload_Expiry')
        CREATE NONCLUSTERED INDEX IX_FddAgentDiagnosticPayload_Expiry
            ON dbo.FddAgentDiagnosticPayload (ExpiresAtUtc)
            INCLUDE (UserId, OwnerType, OwnerId, CreatedAtUtc);

    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.FddAgentDiagnosticPayload') AND name = N'IX_FddAgentDiagnosticPayload_UserOwner')
        CREATE NONCLUSTERED INDEX IX_FddAgentDiagnosticPayload_UserOwner
            ON dbo.FddAgentDiagnosticPayload (UserId, OwnerType, OwnerId);

    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.FddAgentMaintenanceRun') AND name = N'IX_FddAgentMaintenanceRun_OperationStarted')
        CREATE NONCLUSTERED INDEX IX_FddAgentMaintenanceRun_OperationStarted
            ON dbo.FddAgentMaintenanceRun (Operation, StartedAtUtc DESC)
            INCLUDE (Status, ExaminedRows, DeletedRows, SafeErrorCode);

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0
        ROLLBACK TRANSACTION;
    THROW;
END CATCH;
