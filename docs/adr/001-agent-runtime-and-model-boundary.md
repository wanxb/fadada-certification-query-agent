# ADR-001: ChatClientAgent Runtime and Model Boundary

> Status: Accepted
> Date: 2026-08-24

## Context

The project needs a current .NET Agent implementation while keeping model-provider protocol changes outside the domain and application layers. The approved gateway exposes an OpenAI-compatible Responses endpoint, but compatibility must be demonstrated for function calls, streaming, cancellation, usage and errors.

Microsoft Agent Framework 1.19.0 is stable and its `ChatClientAgent` successfully executes tools through an automatically inserted `FunctionInvokingChatClient`. However, the official OpenAI Responses client and the MAF stored-output-disabled adapter remain marked with `OPENAI001` and `MAAI001` evaluation diagnostics.

## Decision

Use a single MAF `ChatClientAgent` as the Agent runtime and `Microsoft.Extensions.AI.IChatClient` as the only model-provider boundary.

For the core implementation:

1. Implement a narrow project-owned `IChatClient` for the approved `/v1/responses` subset.
2. Keep request construction, response parsing, cancellation, timeout, usage and safe error mapping in the provider adapter. Map complete stateless Responses results into the `IChatClient` streaming interface because the approved gateway's SSE behavior is not stable across model routes.
3. Let `ChatClientAgent` own the Agent abstraction and automatic Function Calling pipeline.
4. Keep all Function Tool policy outside the provider adapter.
5. Do not suppress `OPENAI001` or `MAAI001` in production projects.
6. Reconsider the official Responses adapter when it becomes stable and passes the same contract and Eval suite.
7. Accept absolute HTTP and HTTPS model gateway base URLs because the current internal gateway exposes HTTP only; continue rejecting every other URI scheme and keep Fadada transport HTTPS-only.

The live gateway probe passed custom Base URL routing, non-streaming and streaming responses, Function Tool invocation, tool result continuation, disabled provider storage, usage metadata and safe HTTP error surfacing. If a future gateway or package version cannot preserve this contract, stop Agent Core upgrades and choose either a gateway change or a controlled direct `Microsoft.Extensions.AI` loop through a new ADR.

## Consequences

- MAF-specific runtime features remain available without coupling Application or Domain to the provider SDK.
- The project owns a small amount of HTTP/SSE protocol code, but the surface is limited to one approved Responses profile.
- Provider portability is testable through `IChatClient` contract tests.
- Production projects carry no experimental OpenAI adapter reference or diagnostic suppression.
- Live credentials are required only for an explicit compatibility test and are never committed.
- HTTP compatibility trades away transport confidentiality and server authentication. Network isolation is an operational dependency, and HTTPS remains preferred when the gateway supports it.

## Evidence

- `src/Fadada.CertificationQueryAgent.Infrastructure/Ai/ResponsesChatClient.cs`
- `tests/Fadada.CertificationQueryAgent.ContractTests/ResponsesChatClientContractTests.cs`
