# Agent Evaluation Release Gate

## Purpose

The offline gate verifies the current Agent pipeline and its deterministic safety boundaries. It does not compare against a retired implementation and does not claim live-model quality.

Dataset: `agent-golden-1.0.0+security-red-team-1.0.0`, containing 36 synthetic/de-identified cases.

## Absolute Rules

| Metric | Required |
|---|---:|
| Case pass rate | 100% |
| Tool selection accuracy | 100% |
| Argument accuracy | 100% |
| Grounded evidence-state accuracy | 100% |
| Framework evaluation rate | 100% |
| Safety violations | 0 |
| Invalid or forbidden tool calls | 0 |
| Seeded regressions detected | all |

The gate executes `DomainAgentRuntime`, `ChatClientAgent`, Function Tools, the project-owned policy pipeline, MAF deterministic evaluators, and Microsoft.Extensions.AI.Evaluation. The provider fixture is in-memory and no network access is permitted by the default run.

## Run

```powershell
dotnet run --project tests\Fadada.CertificationQueryAgent.Evals\Fadada.CertificationQueryAgent.Evals.csproj `
  -c Release --no-build --no-restore
```

Expected summary shape:

```text
Agent cases=36, passRate=100.00%, gate=True, safetyViolations=0, invalidToolCalls=0, seededRegressionDetected=True, modelQualityClaim=False
```

Sanitized JSON and JUnit artifacts are written under `artifacts/evals/`. They contain case identifiers and aggregate results, not credentials, prompts, raw provider payloads, or real certification information.

## Limitations

The scripted client uses scenario expectations to make the pipeline reproducible. A separate opt-in live profile is required before making claims about natural-language understanding, output quality, provider reliability, latency, or cost. Deterministic ownership, injection, tool allowlist, and evidence checks remain mandatory in every profile.
