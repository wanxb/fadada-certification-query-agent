# ADR-010: Current-Agent Regression Evaluation

> Status: Accepted
> Date: 2026-08-25

## Context

This repository is an Agent architecture learning project built around a deliberately simple read-only query domain. A retired implementation is not a valid permanent release dependency, and comparison against it can make a new Agent look acceptable while still allowing absolute regressions.

## Decision

Evaluate only the current `Fadada.CertificationQueryAgent` runtime against versioned synthetic/de-identified cases and deterministic provider fixtures. The committed offline gate runs the real `DomainAgentRuntime`, Microsoft Agent Framework function-calling path, tool policy pipeline, MAF local checks, and Microsoft.Extensions.AI.Evaluation metrics.

The release gate requires:

- 100% pass rate for the committed 36-case suite.
- Zero safety violations and zero unknown or forbidden tool calls.
- Exact clarification, tool sequence, arguments, evidence status, and framework checks.
- Successful detection of seeded tool-selection and safety regressions.

The scripted offline `IChatClient` sets `supportsModelQualityClaims=false`. It verifies architecture and policy conformance, not semantic generalization, response quality, provider latency, stochastic stability, or production cost. Any live-model quality profile must be explicitly enabled, version-pinned, isolated from the default test suite, and reported separately.

## Consequences

- Release decisions use absolute current behavior rather than improvement over retired code.
- The retired parser and comparison-only targets, models, and reports are removed.
- Security checks remain deterministic and authoritative even if a future LLM judge is added.
- Dataset, Prompt, tool schema, framework, and evaluator changes must be reviewed together.

## Evidence

- `evals/golden/agent-golden.v1.json`
- `evals/red-team/security.v1.json`
- `tests/Fadada.CertificationQueryAgent.Evals/OfflineAgentTarget.cs`
- `tests/Fadada.CertificationQueryAgent.Evals/EvaluationEngine.cs`
- `docs/reports/agent-evaluation-release-gate.md`
