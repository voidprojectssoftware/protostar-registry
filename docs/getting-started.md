---
sidebar_position: 3
title: Getting started (development)
---

# Getting started (development)

How to run the protostar registry locally and sign in to it with the CLI. This covers the full
development loop: prerequisites, the one-time GitHub OAuth setup, running the stack under .NET
Aspire, and authenticating end to end.

The registry is the service users will sync their skills to; the [`protostar` CLI](/cli) is a separate,
disconnected client. (Sync is not built yet — today the working end-to-end path is **authentication**.)
You can run the registry on its own, but to exercise sign-in you will use both.

## Prerequisites

- **.NET 10 SDK**
- **A container runtime** (Docker or Podman) — Aspire starts Postgres in a container.
- **The ASP.NET Core HTTPS dev certificate**, trusted once:
  ```bash
  dotnet dev-certs https --trust
  ```
  The API is served over HTTPS in every environment (OpenIddict rejects plain HTTP), so this is
  required, not optional.

## One-time setup: GitHub OAuth app

Sign-in is federated to GitHub, so you need a GitHub OAuth app. Create it in the
**`voidprojectssoftware` org** (not your personal account) so the whole team can manage it.

1. Go to **`https://github.com/organizations/voidprojectssoftware/settings/applications/new`**
   (or: org → Settings → Developer settings → **OAuth Apps** → New OAuth App). You must be an org
   owner. Pick **OAuth App**, not GitHub App — the registry uses the OAuth App flow.
2. Fill in:
   - **Application name:** `protostar registry (dev)`
   - **Homepage URL:** `https://github.com/voidprojectssoftware/protostar-registry`
   - **Authorization callback URL:** `https://localhost:7443/signin-github`
     The API is pinned to `https://localhost:7443` in dev (see below), so this callback stays stable
     across runs. `/signin-github` is the GitHub handler's default callback path.
3. Create it, then **Generate a new client secret** and copy the secret immediately (shown once).
4. Store the credentials in the AppHost's user-secrets (they stay on your machine, never in the repo):
   ```bash
   cd src/Protostar.Registry.AppHost
   dotnet user-secrets set "Parameters:GitHubClientId" "<client id>"
   dotnet user-secrets set "Parameters:GitHubClientSecret" "<client secret>"
   ```

Without these, the stack still boots (the AppHost defaults the values to `placeholder`), but the
GitHub leg of sign-in will fail. Use a **separate** OAuth app per environment — make another one with
the production callback when you deploy, rather than reusing this dev app.

:::tip Not an owner of the `voidprojectssoftware` org?
Creating the app in the org is preferred so the whole team can manage it, but it
is not required to get sign-in working locally. If you cannot create an org app,
make the OAuth app under **your own GitHub account** (the
[new-OAuth-App page](https://github.com/settings/applications/new)) with the exact
same callback `https://localhost:7443/signin-github`. It works identically for
local development.
:::

## Run the registry

```bash
dotnet run --project src/Protostar.Registry.AppHost
```

This brings up Postgres and the API together and prints the **Aspire dashboard** URL. In the
dashboard you will see two resources, `postgres` and `api`. The API:

- is pinned to **`https://localhost:7443`** (a stable, non-proxied port — this is what `--registry`
  and the GitHub callback point at);
- applies EF Core migrations and seeds the `protostar-cli` OAuth client automatically in Development;
- exposes named links in the dashboard: **API reference (Scalar)** at `/scalar/v1`, **OpenAPI
  document** at `/openapi/v1.json`, **Service metadata** at `/v1/meta`, **OpenID configuration**, and
  **Health checks**.

> The dashboard runs on its own port (e.g. `https://localhost:17254`). That is **not** the API — point
> clients at the `api` resource URL (`https://localhost:7443`), not the dashboard.

:::info 📷 Screenshot slot — `img/aspire-dashboard.png`
The Aspire dashboard on first run, showing the `postgres` and `api` resources and
the named API links. Drop the image into `docs/img/` and replace this admonition
with: `![Aspire dashboard](./img/aspire-dashboard.png)`
:::

Quick check:

```bash
curl -k https://localhost:7443/v1/meta
# {"service":"protostar-registry","version":"0.1.0-alpha.0.N","apiMajors":[1]}
```

## Sign in with the CLI

Build the CLI from source (see [CLI → Build from source](/cli/develop/build-from-source)), then:

```bash
protostar auth login                 # defaults to https://localhost:7443
protostar auth login --provider github   # skip the chooser, go straight to GitHub
protostar auth status
protostar auth logout
```

`auth login` opens your browser to the registry's sign-in chooser, you pick GitHub (or `--provider`
skips the chooser), the registry federates to GitHub, and the resulting tokens are stored in
`~/.protostar/credentials.json` (owner-only permissions; override with `PROTOSTAR_CONFIG_DIR`).
Point at a different registry with `--registry <url>` or `PROTOSTAR_REGISTRY_URL`.
The CLI checks API compatibility (`/v1/meta`) on connect and refuses an unsupported major.

## Tests

```bash
dotnet test
```

Registry integration tests use `WebApplicationFactory` and run database-free (they cover the API
surface, the sign-in chooser, and the OIDC discovery document). Database-backed acceptance scenarios
live in the CLI repo's Reqnroll suite.

## Database and migrations

The schema is EF Core (`RegistryDbContext`) plus OpenIddict's tables. Migrations apply automatically
on startup **in Development only**. In production they are **not** auto-applied — they are run as an
explicit deploy step (typically `dotnet ef database update`, or a generated migration bundle, run by
your deploy pipeline before the new image serves traffic). Keeping production manual avoids a racing
container applying a migration mid-rollout.

Migration commands use the EF Core CLI tool. Install it once if you do not have it:

```bash
dotnet tool install --global dotnet-ef
```

To add a migration after changing the model:

```bash
dotnet ef migrations add <Name> --project src/Protostar.Registry.Api
```

A design-time factory (`RegistryDbContextFactory`) supplies a placeholder connection string so the
tooling can build the model without a running database.

## Releasing

The registry ships as a container image to GHCR, not a binary. release-please drives stable
`:X.Y.Z` + `:latest` images; the edge workflow pushes a rolling `:edge` on every push to `main`. See
[How the registry works → Releasing](./concepts.md#releasing) for the version/release model.

## Troubleshooting

- **`'<' is an invalid start of a value` from the CLI** — you pointed `--registry` at something that
  returned HTML (commonly the Aspire dashboard port) instead of the API. Use `https://localhost:7443`.
- **First run logs `relation "__EFMigrationsHistory" does not exist`** — expected on an empty
  database: EF probes for the migrations table, finds none, then creates the schema. It only happens
  once.
- **Postgres logs `could not accept GSSAPI security context`** — harmless. The Npgsql client tries
  Kerberos/SSPI first on Windows, then falls back to password auth.
- **HTTPS / certificate errors** — run `dotnet dev-certs https --trust`.
- **GitHub sign-in fails or loops** — confirm the OAuth app's callback is exactly
  `https://localhost:7443/signin-github` and that the client id/secret are set in the AppHost's
  user-secrets.
