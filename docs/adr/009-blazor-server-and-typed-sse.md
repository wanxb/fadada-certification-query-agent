# ADR-009: Blazor Interactive Server and Typed SSE

> Status: Accepted
> Date: 2026-08-24

## Context

The internal tool needs a compact authenticated workbench, multi-turn conversation navigation, cancellation, and visible progress. Sending framework-native Agent events or raw model chunks to the browser could expose prompts, arguments, provider payloads, or reasoning traces.

## Decision

Use a Blazor Web App with Interactive Server for the workbench and versioned Minimal APIs for application boundaries. Stream turns as a small typed SSE event set: turn start, safe tool stage, sanitized text delta, completion, cancellation, and stable safe error.

The server maps internal events to the public contract and never sends system prompts, chain-of-thought, raw function arguments/results, credentials, provider conversation IDs, or exception text. Cookie auth, antiforgery, CSP, no-cache headers, ownership checks, and turn concurrency controls apply before streaming starts.

## Consequences

- The UI can show progress without exposing internal Agent state.
- Interactive Server introduces connection lifecycle and server resource considerations.
- T-017 and T-018 must validate reconnect/cancel behavior, safe text rendering, mobile layout, and authenticated SSE semantics.

## Evidence

- `src/Fadada.CertificationQueryAgent.Application/AgentTurns/AgentRuntimeContracts.cs` (`AgentEvent`)
- `src/Fadada.CertificationQueryAgent.Web/Program.cs`
