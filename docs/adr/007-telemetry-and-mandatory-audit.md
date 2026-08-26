# ADR-007: OpenTelemetry and Mandatory Structured Audit

> Status: Accepted
> Date: 2026-08-24

## Context

Traces and metrics are useful for latency, token, cost, and failure analysis, but exporters are best-effort systems and are not an integrity boundary. Tool and external calls need durable accountability even when telemetry is unavailable.

## Decision

Use OpenTelemetry for non-sensitive operational traces, metrics, and correlation. Use a separate SQL-backed structured audit lifecycle for turns, model calls, tool calls, external calls, account administration, and maintenance.

Audit prewrite is mandatory before every external or domain-tool execution. Prewrite failure fails closed. OTLP export failure must not block business execution or weaken audit. Production telemetry excludes prompts, arguments, raw results, credentials, and high-cardinality business identifiers.

## Consequences

- Operators get standard observability without treating an exporter as a system of record.
- Audit storage availability is part of request availability.
- T-015 and T-016 must implement and test the two independent paths.

## Evidence

- `src/Fadada.CertificationQueryAgent.Application/Auditing/AuditContracts.cs`
- `src/Fadada.CertificationQueryAgent.AgentHost/Middleware/ToolPolicyPipeline.cs`
- `src/Fadada.CertificationQueryAgent.Infrastructure/Fadada/ExternalAuditScope.cs`
- `tests/Fadada.CertificationQueryAgent.IntegrationTests/FadadaIntegrationTests.cs`
