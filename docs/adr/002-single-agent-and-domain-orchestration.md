# ADR-002: Single Agent and Deterministic Domain Orchestration

> Status: Accepted
> Date: 2026-08-24

## Context

The project is a read-only professional query Agent with four user-visible capabilities. Person, company, relationship, and seal queries may require several provider calls, but their order, short-circuit rules, evidence aggregation, and error handling are business rules rather than open-ended planning.

## Decision

Use one reusable MAF `ChatClientAgent`. Keep provider call chains in ordinary C# services and do not use MAF Workflow or multiple Agents in the core version.

The model may select one coarse domain tool and supply user-authorized arguments. C# owns all provider endpoint selection, retries, parallelism, status normalization, and final evidence construction. A turn is limited to three model calls and one domain tool call.

## Consequences

- Agent behavior remains easy to evaluate and trace.
- Business correctness does not depend on model planning.
- Workflow/checkpoint experiments require a separate ADR and evidence that they solve a demonstrated problem.
- The architecture intentionally does not teach multi-Agent coordination because the use case does not justify it.

## Evidence

- `src/Fadada.CertificationQueryAgent.AgentHost/Runtime/DomainAgentRuntime.cs`
- `src/Fadada.CertificationQueryAgent.Infrastructure/Fadada/FadadaDomainQueryService.cs`
- `tests/Fadada.CertificationQueryAgent.IntegrationTests/AgentRuntimeIntegrationTests.cs`
