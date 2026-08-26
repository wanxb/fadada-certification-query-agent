# ADR-005: Local Cookie Accounts

> Status: Accepted
> Date: 2026-08-24

## Context

The deployment has no AD domain and serves only a few internal users. IP addresses are shared, mutable network attributes and cannot provide durable identity, session ownership, revocation, or accountable audit records. Full ASP.NET Core Identity UI and its general-purpose schema would add unnecessary surface for this learning project.

## Decision

Use a small application-owned local account store with ASP.NET Core Cookie Authentication. Use `PasswordHasher<TUser>`, normalized unique usernames, lockout, `SecurityStamp`, secure persistent Data Protection keys, antiforgery protection, and login/turn rate limits. All enabled accounts receive the same tool capabilities, but every conversation and diagnostic read remains owner-scoped.

Account creation, reset, disable, and enable are local administrator CLI operations only. Client IP is retained as an audit attribute and abuse signal, never as identity or authorization input.

## Consequences

- Identity remains explicit and revocable without an AD dependency.
- There is no role matrix in the core version.
- Password reset and server key backup become operational responsibilities.
- Authentication is not yet implemented at T-011; T-014 and T-017 must satisfy this ADR before deployment.

## Evidence

- `src/Fadada.CertificationQueryAgent.Application/Authentication/AuthenticationContracts.cs`
- `tools/Fadada.CertificationQueryAgent.Admin/Program.cs`
