# ADR-006: SQL Server 2012 Lab Profile

> Status: Accepted
> Date: 2026-08-24

## Context

The optional development Lab uses a local SQL Server 2012 instance at `localhost/FadadaAgentLab`. This profile exists for compatibility experiments, but its age, support status and transport capabilities must not determine the core Agent architecture or be represented as a modern production recommendation.

Live compatibility checks are explicit and opt-in. Default builds use substitutes and do not assume that a public clone has a SQL Server 2012 instance available.

## Decision

1. Treat SQL Server 2012 as an explicit, opt-in **Lab Profile**, never as the production reference database.
2. Use current `Microsoft.Data.SqlClient` in `Fadada.CertificationQueryAgent.Infrastructure.SqlServer2012`; do not add `System.Data.SqlClient` while the current client satisfies the Lab data contract.
3. Keep SQL client packages and SQL Server 2012 syntax out of Domain, Application, AgentHost and Web request handling. Architecture tests enforce this boundary.
4. Require the Lab connection to target exactly `localhost/FadadaAgentLab`. Do not query or modify PSP databases.
5. Keep application-startup migration disabled. V2 permanent DDL is a separate, reviewed, manually executed operation.
6. Allow `Encrypt=False` only in an isolated local Lab configuration. Do not copy this setting into a production profile.
7. Require supported SQL Server, mandatory transport encryption and validated certificates for a production reference deployment.
8. Make live Lab checks explicit and opt-in; default builds and tests use substitutes and never contact the server.

## Consequences

- The project can test realistic legacy persistence without coupling Agent behavior to an obsolete database platform.
- The Lab demonstrates functional compatibility but does not demonstrate confidentiality or server identity on the database transport.
- Database unavailability must fail closed before authentication state, conversation state or mandatory audit is accepted.
- A future database replacement can implement the same Application persistence ports without changing tools, policy, Agent runtime or Evals.
- Operations must track the Lab exception explicitly and prevent its connection settings from becoming a deployment template.

## Alternatives Considered

### Use `System.Data.SqlClient` for legacy compatibility

Rejected. The current supported client passed all required operations. A legacy client would increase maintenance and security debt and would not turn the observed unencrypted Lab transport into an acceptable production channel.

### Make SQL Server 2012 the general persistence baseline

Rejected. It would force obsolete capabilities and security assumptions into a reference architecture intended to teach current enterprise Agent practices.

### Skip the real database test

Rejected. Driver, type, transaction and concurrency assumptions needed executable evidence before V2 schema and repository work.

## Revisit Conditions

Re-run the executable probe and update this ADR when the Lab host, TLS policy, `Microsoft.Data.SqlClient` version, or production database target changes. Moving any environment out of the Lab Profile requires a supported server version plus successful mandatory encryption with certificate validation.

## Evidence

- `src/Fadada.CertificationQueryAgent.Infrastructure.SqlServer2012`
- `tests/Fadada.CertificationQueryAgent.IntegrationTests/SqlServer2012PersistenceTests.cs`
