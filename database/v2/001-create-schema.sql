/*
  FDD Domain Agent V2 bootstrap schema, version 1.
  SQL Server 2012 compatible. Review and execute manually in the dedicated V2 database.
  This script is isolated from retired schemas and PSP objects.
*/
SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRY
    BEGIN TRANSACTION;

    IF OBJECT_ID(N'dbo.FddAgentSchemaVersion', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.FddAgentSchemaVersion
        (
            Component NVARCHAR(64) NOT NULL,
            SchemaVersion INT NOT NULL,
            ScriptId NVARCHAR(128) NOT NULL,
            AppliedAtUtc DATETIME2(3) NOT NULL,
            CONSTRAINT PK_FddAgentSchemaVersion PRIMARY KEY CLUSTERED (Component),
            CONSTRAINT CK_FddAgentSchemaVersion_Positive CHECK (SchemaVersion > 0)
        );
    END;

    IF OBJECT_ID(N'dbo.FddAgentUser', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.FddAgentUser
        (
            Id UNIQUEIDENTIFIER NOT NULL,
            UserName NVARCHAR(128) NOT NULL,
            NormalizedUserName NVARCHAR(128) NOT NULL,
            DisplayName NVARCHAR(128) NOT NULL,
            PasswordHash NVARCHAR(1024) NOT NULL,
            SecurityStamp NVARCHAR(128) NOT NULL,
            IsActive BIT NOT NULL CONSTRAINT DF_FddAgentUser_IsActive DEFAULT (1),
            AccessFailedCount INT NOT NULL CONSTRAINT DF_FddAgentUser_AccessFailedCount DEFAULT (0),
            LockoutEndUtc DATETIME2(3) NULL,
            LastLoginAtUtc DATETIME2(3) NULL,
            CreatedAtUtc DATETIME2(3) NOT NULL,
            UpdatedAtUtc DATETIME2(3) NOT NULL,
            RowVersion ROWVERSION NOT NULL,
            CONSTRAINT PK_FddAgentUser PRIMARY KEY CLUSTERED (Id),
            CONSTRAINT CK_FddAgentUser_AccessFailedCount CHECK (AccessFailedCount >= 0),
            CONSTRAINT CK_FddAgentUser_Names CHECK (LEN(UserName) > 0 AND LEN(NormalizedUserName) > 0)
        );
    END;

    IF OBJECT_ID(N'dbo.FddAgentConversation', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.FddAgentConversation
        (
            Id UNIQUEIDENTIFIER NOT NULL,
            UserId UNIQUEIDENTIFIER NOT NULL,
            Title NVARCHAR(200) NOT NULL,
            Status NVARCHAR(32) NOT NULL,
            NextSequenceNumber BIGINT NOT NULL CONSTRAINT DF_FddAgentConversation_NextSequence DEFAULT (1),
            CreatedAtUtc DATETIME2(3) NOT NULL,
            UpdatedAtUtc DATETIME2(3) NOT NULL,
            ArchivedAtUtc DATETIME2(3) NULL,
            RowVersion ROWVERSION NOT NULL,
            CONSTRAINT PK_FddAgentConversation PRIMARY KEY CLUSTERED (Id),
            CONSTRAINT FK_FddAgentConversation_User FOREIGN KEY (UserId) REFERENCES dbo.FddAgentUser (Id),
            CONSTRAINT CK_FddAgentConversation_Status CHECK (Status IN (N'Active', N'Archived')),
            CONSTRAINT CK_FddAgentConversation_NextSequence CHECK (NextSequenceNumber > 0),
            CONSTRAINT CK_FddAgentConversation_Archive CHECK
                ((Status = N'Active' AND ArchivedAtUtc IS NULL) OR (Status = N'Archived' AND ArchivedAtUtc IS NOT NULL))
        );
    END;

    IF OBJECT_ID(N'dbo.FddAgentSecurityEvent', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.FddAgentSecurityEvent
        (
            Id UNIQUEIDENTIFIER NOT NULL,
            TargetUserId UNIQUEIDENTIFIER NOT NULL,
            EventType NVARCHAR(32) NOT NULL,
            Actor NVARCHAR(128) NOT NULL,
            OccurredAtUtc DATETIME2(3) NOT NULL,
            CONSTRAINT PK_FddAgentSecurityEvent PRIMARY KEY CLUSTERED (Id),
            CONSTRAINT FK_FddAgentSecurityEvent_User FOREIGN KEY (TargetUserId) REFERENCES dbo.FddAgentUser (Id),
            CONSTRAINT CK_FddAgentSecurityEvent_Values CHECK (LEN(EventType) > 0 AND LEN(Actor) > 0)
        );
    END;

    IF OBJECT_ID(N'dbo.FddAgentTurn', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.FddAgentTurn
        (
            Id UNIQUEIDENTIFIER NOT NULL,
            ConversationId UNIQUEIDENTIFIER NOT NULL,
            TraceId UNIQUEIDENTIFIER NOT NULL,
            UserMessageId UNIQUEIDENTIFIER NOT NULL,
            PromptVersion NVARCHAR(64) NOT NULL,
            PromptSha256 CHAR(64) NOT NULL,
            ModelProfile NVARCHAR(128) NOT NULL,
            ToolSetVersion NVARCHAR(64) NOT NULL,
            Status NVARCHAR(32) NOT NULL,
            ModelCallCount INT NOT NULL CONSTRAINT DF_FddAgentTurn_ModelCalls DEFAULT (0),
            ToolCallCount INT NOT NULL CONSTRAINT DF_FddAgentTurn_ToolCalls DEFAULT (0),
            InputTokens INT NOT NULL CONSTRAINT DF_FddAgentTurn_InputTokens DEFAULT (0),
            OutputTokens INT NOT NULL CONSTRAINT DF_FddAgentTurn_OutputTokens DEFAULT (0),
            EstimatedCost DECIMAL(19, 8) NOT NULL CONSTRAINT DF_FddAgentTurn_Cost DEFAULT (0),
            StartedAtUtc DATETIME2(3) NOT NULL,
            CompletedAtUtc DATETIME2(3) NULL,
            SafeErrorCode NVARCHAR(64) NULL,
            RowVersion ROWVERSION NOT NULL,
            CONSTRAINT PK_FddAgentTurn PRIMARY KEY CLUSTERED (Id),
            CONSTRAINT FK_FddAgentTurn_Conversation FOREIGN KEY (ConversationId) REFERENCES dbo.FddAgentConversation (Id),
            CONSTRAINT CK_FddAgentTurn_Status CHECK (Status IN (N'Started', N'Succeeded', N'Rejected', N'Failed', N'Cancelled')),
            CONSTRAINT CK_FddAgentTurn_Counts CHECK
                (ModelCallCount >= 0 AND ModelCallCount <= 4 AND ToolCallCount >= 0 AND ToolCallCount <= 3 AND InputTokens >= 0 AND OutputTokens >= 0),
            CONSTRAINT CK_FddAgentTurn_Cost CHECK (EstimatedCost >= 0)
        );
    END;

    IF OBJECT_ID(N'dbo.FddAgentMessage', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.FddAgentMessage
        (
            Id UNIQUEIDENTIFIER NOT NULL,
            ConversationId UNIQUEIDENTIFIER NOT NULL,
            TurnId UNIQUEIDENTIFIER NULL,
            Role NVARCHAR(16) NOT NULL,
            Content NVARCHAR(MAX) NOT NULL,
            SequenceNumber BIGINT NOT NULL,
            CreatedAtUtc DATETIME2(3) NOT NULL,
            CONSTRAINT PK_FddAgentMessage PRIMARY KEY CLUSTERED (Id),
            CONSTRAINT FK_FddAgentMessage_Conversation FOREIGN KEY (ConversationId) REFERENCES dbo.FddAgentConversation (Id),
            CONSTRAINT FK_FddAgentMessage_Turn FOREIGN KEY (TurnId) REFERENCES dbo.FddAgentTurn (Id),
            CONSTRAINT CK_FddAgentMessage_Role CHECK (Role IN (N'User', N'Assistant')),
            CONSTRAINT CK_FddAgentMessage_Sequence CHECK (SequenceNumber > 0)
        );
    END;

    IF OBJECT_ID(N'dbo.FddAgentModelCall', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.FddAgentModelCall
        (
            Id UNIQUEIDENTIFIER NOT NULL,
            TurnId UNIQUEIDENTIFIER NOT NULL,
            AttemptNumber INT NOT NULL,
            Provider NVARCHAR(64) NOT NULL,
            ModelName NVARCHAR(128) NOT NULL,
            Status NVARCHAR(32) NOT NULL,
            InputTokens INT NOT NULL CONSTRAINT DF_FddAgentModelCall_InputTokens DEFAULT (0),
            OutputTokens INT NOT NULL CONSTRAINT DF_FddAgentModelCall_OutputTokens DEFAULT (0),
            EstimatedCost DECIMAL(19, 8) NOT NULL CONSTRAINT DF_FddAgentModelCall_Cost DEFAULT (0),
            DurationMilliseconds BIGINT NULL,
            StartedAtUtc DATETIME2(3) NOT NULL,
            CompletedAtUtc DATETIME2(3) NULL,
            SafeErrorCode NVARCHAR(64) NULL,
            CONSTRAINT PK_FddAgentModelCall PRIMARY KEY CLUSTERED (Id),
            CONSTRAINT FK_FddAgentModelCall_Turn FOREIGN KEY (TurnId) REFERENCES dbo.FddAgentTurn (Id),
            CONSTRAINT CK_FddAgentModelCall_Attempt CHECK (AttemptNumber BETWEEN 1 AND 4),
            CONSTRAINT CK_FddAgentModelCall_Status CHECK (Status IN (N'Started', N'Succeeded', N'Rejected', N'Failed', N'Cancelled')),
            CONSTRAINT CK_FddAgentModelCall_Metrics CHECK
                (InputTokens >= 0 AND OutputTokens >= 0 AND EstimatedCost >= 0 AND (DurationMilliseconds IS NULL OR DurationMilliseconds >= 0))
        );
    END;

    IF OBJECT_ID(N'dbo.FddAgentToolCall', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.FddAgentToolCall
        (
            Id UNIQUEIDENTIFIER NOT NULL,
            TurnId UNIQUEIDENTIFIER NOT NULL,
            SequenceNumber INT NOT NULL,
            ToolName NVARCHAR(64) NOT NULL,
            PolicyDecision NVARCHAR(32) NOT NULL,
            PolicyErrorCode NVARCHAR(64) NULL,
            SafeArgumentsSummary NVARCHAR(1000) NULL,
            Status NVARCHAR(32) NOT NULL,
            DurationMilliseconds BIGINT NULL,
            SafeResultSummary NVARCHAR(1000) NULL,
            StartedAtUtc DATETIME2(3) NOT NULL,
            CompletedAtUtc DATETIME2(3) NULL,
            SafeErrorCode NVARCHAR(64) NULL,
            CONSTRAINT PK_FddAgentToolCall PRIMARY KEY CLUSTERED (Id),
            CONSTRAINT FK_FddAgentToolCall_Turn FOREIGN KEY (TurnId) REFERENCES dbo.FddAgentTurn (Id),
            CONSTRAINT CK_FddAgentToolCall_Sequence CHECK (SequenceNumber BETWEEN 1 AND 3),
            CONSTRAINT CK_FddAgentToolCall_Decision CHECK (PolicyDecision IN (N'Allowed', N'Rejected')),
            CONSTRAINT CK_FddAgentToolCall_Status CHECK (Status IN (N'Started', N'Succeeded', N'Rejected', N'Failed', N'Cancelled')),
            CONSTRAINT CK_FddAgentToolCall_Duration CHECK (DurationMilliseconds IS NULL OR DurationMilliseconds >= 0)
        );
    END;

    IF OBJECT_ID(N'dbo.FddAgentExternalCall', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.FddAgentExternalCall
        (
            Id UNIQUEIDENTIFIER NOT NULL,
            ToolCallId UNIQUEIDENTIFIER NOT NULL,
            SequenceNumber INT NOT NULL,
            EndpointKey NVARCHAR(64) NOT NULL,
            HttpMethod NVARCHAR(8) NOT NULL,
            Status NVARCHAR(32) NOT NULL,
            HttpStatusCode INT NULL,
            BusinessCode NVARCHAR(64) NULL,
            DurationMilliseconds BIGINT NULL,
            StartedAtUtc DATETIME2(3) NOT NULL,
            CompletedAtUtc DATETIME2(3) NULL,
            SafeErrorCode NVARCHAR(64) NULL,
            CONSTRAINT PK_FddAgentExternalCall PRIMARY KEY CLUSTERED (Id),
            CONSTRAINT FK_FddAgentExternalCall_ToolCall FOREIGN KEY (ToolCallId) REFERENCES dbo.FddAgentToolCall (Id),
            CONSTRAINT CK_FddAgentExternalCall_Sequence CHECK (SequenceNumber > 0),
            CONSTRAINT CK_FddAgentExternalCall_Method CHECK (HttpMethod IN (N'GET', N'POST')),
            CONSTRAINT CK_FddAgentExternalCall_Status CHECK (Status IN (N'Started', N'Succeeded', N'Rejected', N'Failed', N'Cancelled')),
            CONSTRAINT CK_FddAgentExternalCall_HttpStatus CHECK (HttpStatusCode IS NULL OR HttpStatusCode BETWEEN 100 AND 599),
            CONSTRAINT CK_FddAgentExternalCall_Duration CHECK (DurationMilliseconds IS NULL OR DurationMilliseconds >= 0)
        );
    END;

    IF OBJECT_ID(N'dbo.FddAgentSessionState', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.FddAgentSessionState
        (
            ConversationId UNIQUEIDENTIFIER NOT NULL,
            Format NVARCHAR(64) NOT NULL,
            StateVersion NVARCHAR(64) NOT NULL,
            ProtectedPayload VARBINARY(MAX) NOT NULL,
            UpdatedAtUtc DATETIME2(3) NOT NULL,
            RowVersion ROWVERSION NOT NULL,
            CONSTRAINT PK_FddAgentSessionState PRIMARY KEY CLUSTERED (ConversationId),
            CONSTRAINT FK_FddAgentSessionState_Conversation FOREIGN KEY (ConversationId) REFERENCES dbo.FddAgentConversation (Id)
        );
    END;

    IF OBJECT_ID(N'dbo.FddAgentDiagnosticPayload', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.FddAgentDiagnosticPayload
        (
            Id UNIQUEIDENTIFIER NOT NULL,
            UserId UNIQUEIDENTIFIER NOT NULL,
            OwnerType NVARCHAR(32) NOT NULL,
            OwnerId UNIQUEIDENTIFIER NOT NULL,
            ProtectedPayload VARBINARY(MAX) NOT NULL,
            ExpiresAtUtc DATETIME2(3) NOT NULL,
            CreatedAtUtc DATETIME2(3) NOT NULL,
            CONSTRAINT PK_FddAgentDiagnosticPayload PRIMARY KEY CLUSTERED (Id),
            CONSTRAINT FK_FddAgentDiagnosticPayload_User FOREIGN KEY (UserId) REFERENCES dbo.FddAgentUser (Id),
            CONSTRAINT CK_FddAgentDiagnosticPayload_OwnerType CHECK (OwnerType IN (N'Turn', N'ModelCall', N'ToolCall', N'ExternalCall')),
            CONSTRAINT CK_FddAgentDiagnosticPayload_Expiry CHECK (ExpiresAtUtc > CreatedAtUtc)
        );
    END;

    IF OBJECT_ID(N'dbo.FddAgentMaintenanceRun', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.FddAgentMaintenanceRun
        (
            Id UNIQUEIDENTIFIER NOT NULL,
            Operation NVARCHAR(64) NOT NULL,
            ExaminedRows INT NOT NULL CONSTRAINT DF_FddAgentMaintenanceRun_Examined DEFAULT (0),
            DeletedRows INT NOT NULL CONSTRAINT DF_FddAgentMaintenanceRun_Deleted DEFAULT (0),
            Status NVARCHAR(32) NOT NULL,
            StartedAtUtc DATETIME2(3) NOT NULL,
            CompletedAtUtc DATETIME2(3) NULL,
            SafeErrorCode NVARCHAR(64) NULL,
            CONSTRAINT PK_FddAgentMaintenanceRun PRIMARY KEY CLUSTERED (Id),
            CONSTRAINT CK_FddAgentMaintenanceRun_Counts CHECK (ExaminedRows >= 0 AND DeletedRows >= 0 AND DeletedRows <= ExaminedRows),
            CONSTRAINT CK_FddAgentMaintenanceRun_Status CHECK (Status IN (N'Started', N'Succeeded', N'Failed'))
        );
    END;

    IF EXISTS
    (
        SELECT 1
        FROM dbo.FddAgentSchemaVersion
        WHERE Component = N'FddDomainAgent' AND SchemaVersion NOT IN (1, 2)
    )
    BEGIN
        RAISERROR (N'FddDomainAgent schema version is not compatible with script 001.', 16, 1);
    END;

    IF NOT EXISTS
    (
        SELECT 1
        FROM dbo.FddAgentSchemaVersion
        WHERE Component = N'FddDomainAgent'
    )
    BEGIN
        INSERT dbo.FddAgentSchemaVersion (Component, SchemaVersion, ScriptId, AppliedAtUtc)
        VALUES (N'FddDomainAgent', 1, N'001-create-schema', SYSUTCDATETIME());
    END;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0
        ROLLBACK TRANSACTION;
    THROW;
END CATCH;
