# V2 Database Scripts

These scripts define the isolated FDD Domain Agent schema. They are SQL Server 2012 compatible so the approved Lab profile can exercise persistence, but SQL Server 2012 is not the production reference platform.

## Safety boundary

- Execute only after reviewing the resolved server and database. The approved Lab target is the dedicated `FadadaAgentLab` database; PSP databases are out of scope.
- Scripts are manual operations. Web, Admin, and background services must never create or migrate schema during startup.
- `001-create-schema.sql` creates the version 1 baseline, `002-create-indexes.sql` adds indexes, and `004-enable-bounded-multi-tool-turns.sql` transactionally upgrades the schema to version 2.
- `003-readiness-check.sql` is read-only and is the only script suitable for application readiness logic.
- The scripts create only `dbo.FddAgent*` objects. They do not reference, alter, delete, or migrate V1/PSP data.
- Back up the target database and test rollback/recovery procedures before a production-reference deployment.

## Review and execution order

1. Confirm a supported target or explicitly select the isolated SQL Server 2012 Lab profile.
2. Review the scripts and verify that the connection targets only the intended database.
3. Execute `001-create-schema.sql` with a dedicated deployment identity.
4. Execute `002-create-indexes.sql`.
5. Execute `004-enable-bounded-multi-tool-turns.sql` to allow up to three audited tools and four model calls per turn.
6. Execute `003-readiness-check.sql` and require one row with `IsReady = 1` and `SchemaVersion = 2`.
7. Grant the runtime identity only the DML permissions required by the V2 repositories; do not grant DDL or access to unrelated schemas/databases.

No rollback script is supplied because dropping populated security, conversation, diagnostic, or audit tables is destructive. Rollback is an operator-controlled database restore or a separately reviewed forward migration.
