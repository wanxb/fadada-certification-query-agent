/*
  Read-only V2 readiness check for SQL Server 2012 or later.
  Returns one row when schema version 2 and every required table are present.
*/
SET NOCOUNT ON;

DECLARE @MissingObjects NVARCHAR(MAX) = N'';

IF OBJECT_ID(N'dbo.FddAgentSchemaVersion', N'U') IS NULL SET @MissingObjects = @MissingObjects + N' FddAgentSchemaVersion';
IF OBJECT_ID(N'dbo.FddAgentUser', N'U') IS NULL SET @MissingObjects = @MissingObjects + N' FddAgentUser';
IF OBJECT_ID(N'dbo.FddAgentSecurityEvent', N'U') IS NULL SET @MissingObjects = @MissingObjects + N' FddAgentSecurityEvent';
IF OBJECT_ID(N'dbo.FddAgentConversation', N'U') IS NULL SET @MissingObjects = @MissingObjects + N' FddAgentConversation';
IF OBJECT_ID(N'dbo.FddAgentMessage', N'U') IS NULL SET @MissingObjects = @MissingObjects + N' FddAgentMessage';
IF OBJECT_ID(N'dbo.FddAgentTurn', N'U') IS NULL SET @MissingObjects = @MissingObjects + N' FddAgentTurn';
IF OBJECT_ID(N'dbo.FddAgentModelCall', N'U') IS NULL SET @MissingObjects = @MissingObjects + N' FddAgentModelCall';
IF OBJECT_ID(N'dbo.FddAgentToolCall', N'U') IS NULL SET @MissingObjects = @MissingObjects + N' FddAgentToolCall';
IF OBJECT_ID(N'dbo.FddAgentExternalCall', N'U') IS NULL SET @MissingObjects = @MissingObjects + N' FddAgentExternalCall';
IF OBJECT_ID(N'dbo.FddAgentSessionState', N'U') IS NULL SET @MissingObjects = @MissingObjects + N' FddAgentSessionState';
IF OBJECT_ID(N'dbo.FddAgentDiagnosticPayload', N'U') IS NULL SET @MissingObjects = @MissingObjects + N' FddAgentDiagnosticPayload';
IF OBJECT_ID(N'dbo.FddAgentMaintenanceRun', N'U') IS NULL SET @MissingObjects = @MissingObjects + N' FddAgentMaintenanceRun';

IF LEN(@MissingObjects) > 0
BEGIN
    RAISERROR (N'FddDomainAgent V2 schema is not ready. Missing:%s', 16, 1, @MissingObjects);
    RETURN;
END;

IF NOT EXISTS
(
    SELECT 1
    FROM dbo.FddAgentSchemaVersion
    WHERE Component = N'FddDomainAgent'
      AND SchemaVersion = 2
      AND ScriptId = N'004-enable-bounded-multi-tool-turns'
)
BEGIN
    RAISERROR (N'FddDomainAgent V2 schema version is not supported. Expected version 2.', 16, 1);
    RETURN;
END;

SELECT
    CAST(1 AS BIT) AS IsReady,
    N'FddDomainAgent' AS Component,
    CAST(2 AS INT) AS SchemaVersion,
    CAST(SERVERPROPERTY(N'ProductVersion') AS NVARCHAR(128)) AS ServerVersion,
    (SELECT compatibility_level FROM sys.databases WHERE name = DB_NAME()) AS CompatibilityLevel;
