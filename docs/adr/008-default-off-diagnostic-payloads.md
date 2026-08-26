# ADR-008: Default-off Encrypted Diagnostic Payloads

> Status: Accepted
> Date: 2026-08-24

## Context

Raw prompts, model responses, tool arguments, and provider payloads can accelerate incident diagnosis but contain personal data, untrusted content, and potentially credentials. Retaining them by default would conflict with data minimization.

## Decision

Keep raw diagnostic capture disabled by default. When an administrator explicitly enables it for a bounded investigation, encrypt payloads with a separately protected key, bind records to the owning user and turn, restrict reads, audit access, and delete them after seven days.

Normal logs, traces, Eval artifacts, and durable audit records contain only identifiers, versions, counts, safe status/error families, hashes, and timings. They must not fall back to raw content when diagnostic persistence fails.

## Consequences

- Routine operation minimizes sensitive data retention.
- Some failures cannot be reconstructed without an explicitly enabled capture window.
- T-015 must implement encryption, TTL cleanup, access audit, and failure isolation before this capability is usable.

## Evidence

- `src/Fadada.CertificationQueryAgent.Application/Persistence/PersistenceContracts.cs` (`DiagnosticPayload`, `IDiagnosticPayloadStore`)
- `docs/reports/agent-evaluation-release-gate.md`
