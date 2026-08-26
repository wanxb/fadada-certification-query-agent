# ADR-003: Four Coarse Read-only Function Tools

> Status: Accepted
> Date: 2026-08-24

## Context

Exposing provider endpoints directly would enlarge the model-controlled surface, leak transport concepts into prompts, and make write-call exclusion difficult to prove. The user needs four domain outcomes, not access to arbitrary provider APIs.

## Decision

Expose exactly `query_person`, `query_company`, `query_relationship`, and `query_seals`. Each function has a strict JSON Schema with `additionalProperties: false`. No URL, HTTP method, SQL, provider endpoint, credential, or generic execution parameter is accepted.

The endpoint catalog is an immutable seven-entry read-only set, including token acquisition. Exact-set architecture tests guard both lists. Adding or changing a tool requires dataset, threat-model, and release-gate review.

## Consequences

- Tool descriptions match business intent and remain provider-independent.
- The server can prove that write operations and dynamic endpoints are absent.
- Provider-specific orchestration remains testable C# code.
- A future MCP adapter must reuse these contracts and cannot introduce dynamic discovery by default.

## Evidence

- `src/Fadada.CertificationQueryAgent.Application/DomainTools/DomainToolRegistry.cs`
- `src/Fadada.CertificationQueryAgent.Infrastructure/Fadada/FadadaEndpointCatalog.cs`
- `tests/Fadada.CertificationQueryAgent.ArchitectureTests/ProjectDependencyTests.cs`
