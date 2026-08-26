/*
  Schema version 2 migration for bounded multi-tool Agent turns.
  SQL Server 2012 compatible. Execute manually after reviewing the target database.
*/
SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRY
    BEGIN TRANSACTION;

    IF OBJECT_ID(N'dbo.FddAgentSchemaVersion', N'U') IS NULL OR
       NOT EXISTS
       (
           SELECT 1
           FROM dbo.FddAgentSchemaVersion
           WHERE Component = N'FddDomainAgent' AND SchemaVersion IN (1, 2)
       )
        RAISERROR (N'FddDomainAgent schema version 1 or 2 is required.', 16, 1);

    IF OBJECT_ID(N'dbo.FddAgentTurn', N'U') IS NULL OR
       OBJECT_ID(N'dbo.FddAgentModelCall', N'U') IS NULL OR
       OBJECT_ID(N'dbo.FddAgentToolCall', N'U') IS NULL
        RAISERROR (N'Required Agent audit tables are missing.', 16, 1);

    IF EXISTS
    (
        SELECT 1
        FROM sys.check_constraints
        WHERE parent_object_id = OBJECT_ID(N'dbo.FddAgentTurn')
          AND name = N'CK_FddAgentTurn_Counts'
    )
        ALTER TABLE dbo.FddAgentTurn DROP CONSTRAINT CK_FddAgentTurn_Counts;

    ALTER TABLE dbo.FddAgentTurn WITH CHECK
        ADD CONSTRAINT CK_FddAgentTurn_Counts CHECK
            (ModelCallCount >= 0 AND ModelCallCount <= 4 AND ToolCallCount >= 0 AND ToolCallCount <= 3 AND InputTokens >= 0 AND OutputTokens >= 0);
    ALTER TABLE dbo.FddAgentTurn CHECK CONSTRAINT CK_FddAgentTurn_Counts;

    IF EXISTS
    (
        SELECT 1
        FROM sys.check_constraints
        WHERE parent_object_id = OBJECT_ID(N'dbo.FddAgentModelCall')
          AND name = N'CK_FddAgentModelCall_Attempt'
    )
        ALTER TABLE dbo.FddAgentModelCall DROP CONSTRAINT CK_FddAgentModelCall_Attempt;

    ALTER TABLE dbo.FddAgentModelCall WITH CHECK
        ADD CONSTRAINT CK_FddAgentModelCall_Attempt CHECK (AttemptNumber BETWEEN 1 AND 4);
    ALTER TABLE dbo.FddAgentModelCall CHECK CONSTRAINT CK_FddAgentModelCall_Attempt;

    IF EXISTS
    (
        SELECT 1
        FROM sys.check_constraints
        WHERE parent_object_id = OBJECT_ID(N'dbo.FddAgentToolCall')
          AND name = N'CK_FddAgentToolCall_Sequence'
    )
        ALTER TABLE dbo.FddAgentToolCall DROP CONSTRAINT CK_FddAgentToolCall_Sequence;

    ALTER TABLE dbo.FddAgentToolCall WITH CHECK
        ADD CONSTRAINT CK_FddAgentToolCall_Sequence CHECK (SequenceNumber BETWEEN 1 AND 3);
    ALTER TABLE dbo.FddAgentToolCall CHECK CONSTRAINT CK_FddAgentToolCall_Sequence;

    UPDATE dbo.FddAgentSchemaVersion
    SET SchemaVersion = 2,
        ScriptId = N'004-enable-bounded-multi-tool-turns',
        AppliedAtUtc = SYSUTCDATETIME()
    WHERE Component = N'FddDomainAgent'
      AND (SchemaVersion <> 2 OR ScriptId <> N'004-enable-bounded-multi-tool-turns');

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
