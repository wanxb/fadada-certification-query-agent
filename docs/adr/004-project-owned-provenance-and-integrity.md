# ADR-004: Project-owned Provenance and Integrity Labels

> Status: Accepted
> Date: 2026-08-24

## Context

Prompt instructions cannot establish that a mobile number, person name, or company name came from the authenticated user in the active conversation. The selected .NET stack does not currently provide a stable FIDES-style integrity/provenance primitive suitable for this policy.

## Decision

Keep provenance and integrity as project-owned Application contracts. Every tool argument must match an active `UserProvidedValue` scoped to the same `UserId` and `ConversationId`, with canonicalization and confirmation state recorded. Label system, user-authorized, external-untrusted, and secret content explicitly.

Resolve provenance candidate-first: after schema validation, take each proposed tool argument and verify it directly against safe user-authored messages in the active conversation. Do not pre-extract names or company names with marker-dependent grammar such as `姓名：` or `查询某公司`. Text candidates must occur in normalized user text; mobile candidates may use spaces or hyphens in the original message but must canonicalize to the same 11-digit value. Assistant messages and instruction-shaped user messages never establish provenance.

The tool policy fails closed on missing, stale, inferred-only, cross-user, or cross-conversation provenance. External tool results remain untrusted and are sanitized before returning to the model.

## Consequences

- Security does not depend on the model repeating or interpreting attribution correctly.
- Natural-language phrasing is not constrained by field labels or query-command templates.
- A model-invented or altered argument still fails closed because candidate resolution requires a matching user-authored source message.
- Persistence implementations must preserve user/conversation scope and active-value semantics.
- The project owns a small security abstraction that must be revisited if a stable standard becomes available.

## Evidence

- `src/Fadada.CertificationQueryAgent.Application/DomainTools/ToolPolicyContracts.cs`
- `src/Fadada.CertificationQueryAgent.Application/DomainTools/ProvenanceCanonicalizer.cs`
- `src/Fadada.CertificationQueryAgent.AgentHost/Middleware/CanonicalUserProvenanceStore.cs`
- `src/Fadada.CertificationQueryAgent.AgentHost/Middleware/ToolPolicyPipeline.cs`
- `tests/Fadada.CertificationQueryAgent.UnitTests/ToolPolicyPipelineTests.cs`
