---
sidebar_position: 2
title: How the registry works
---

# How the registry works

A tour of the pieces and the two design decisions that matter most: **how
identity works**, and **how the registry and CLI stay compatible without being
locked together.**

## Architecture at a glance

| Piece | What it is |
|---|---|
| `Protostar.Registry.Api` | ASP.NET Core minimal API (.NET 10): OpenIddict + GitHub federation + EF Core. |
| `Protostar.Registry.AppHost` | .NET Aspire orchestrator — brings up Postgres and the API together with a dashboard, OpenTelemetry, and health checks. |
| `Protostar.Registry.ServiceDefaults` | Shared Aspire defaults: OTel, health, and resilience. |
| Postgres | Storage for users and OpenIddict's tables, via EF Core. |

## Identity: OpenIddict, federated to GitHub

The registry is its **own** OAuth2/OIDC authorization server. OpenIddict owns the
`User` records and issues the access/refresh tokens the CLI stores. But the actual
login step is **delegated to GitHub** — so the registry never sees or stores a
password.

The CLI signs in with the **Authorization Code flow + PKCE over a loopback
redirect** (RFC 8252). In plain terms:

1. The CLI sends you to the registry's `/connect/authorize`.
2. If you are not already signed in, the registry challenges GitHub.
3. GitHub returns you to the registry, which issues an authorization code to the
   CLI's one-time `localhost` callback.
4. The CLI exchanges that code (plus its PKCE verifier) at `/connect/token` for
   tokens.

### The API endpoints

| Endpoint | Purpose |
|---|---|
| `/connect/authorize` | OAuth2 authorization (challenges GitHub if needed) |
| `/connect/token` | Exchange an authorization code or refresh token for tokens |
| `/connect/userinfo` | The identified user behind an access token |
| `/connect/logout` | End the session |
| `/.well-known/openid-configuration` | OIDC discovery document |
| `/v1/meta` | Service version + supported API majors (the compatibility contract) |

:::tip Live API reference
A running registry serves an interactive API reference (Scalar) at **`/scalar/v1`**
and the raw OpenAPI document at **`/openapi/v1.json`**. When you
[run it locally](./getting-started.md) these show up as named links in the Aspire
dashboard.
:::

## Compatibility: an API contract, not lockstep versions

The registry and the CLI **version independently**. Each repo drives its own
version from git tags via [MinVer](https://github.com/adamralph/minver) and ships
releases via [release-please](https://github.com/googleapis/release-please). They
are deliberately not pinned to each other.

Instead, compatibility is an **API contract**. The HTTP API is served under `/v1`,
and `GET /v1/meta` advertises the API majors the running registry supports:

```bash
curl -k https://localhost:7443/v1/meta
# {"service":"protostar-registry","version":"0.1.0-alpha.0.N","apiMajors":[1]}
```

The CLI targets an API major and **checks it on connect**, failing with an upgrade
hint on a mismatch rather than breaking halfway through a command. A breaking wire
change bumps the API major (a new `/v2` group); during a transition window the
registry supports both the current and previous major.

:::tip Why this matters
You can ship the registry and the CLI on their own cadences. A user on an older
CLI keeps working as long as the registry still serves an API major their CLI
understands — and gets a clear "please update" message the moment it doesn't.
:::

## Releasing

The registry ships as a **container image** to GHCR, not a binary:

- **stable** — Conventional Commits drive a release-please Release PR; merging it
  tags `vX.Y.Z` and the workflow builds and pushes `:X.Y.Z` and `:latest`.
- **edge** — every change to `main` rebuilds and pushes a rolling `:edge` image,
  versioned by MinVer (`0.X.Y-alpha.0.N`). The `edge` tag does not start with `v`,
  so MinVer ignores it and the channels never collide.

This mirrors the [CLI's release model](/cli/develop/releasing) — same automation,
different artifact.
