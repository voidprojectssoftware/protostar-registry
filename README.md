# protostar-registry

The registry side of [protostar](https://github.com/voidprojectssoftware/protostar) —
the service that identified users sync their skills to, and that the refinement loop runs
against. This repo is the API + its datastore; the CLI is a separate, disconnected client.

protostar's loop: **use → sync → refine → suggest → adopt → use.** The registry is where
synced skills live, get tagged to a user, and route suggestions back.

## Status

Built incrementally, one ticket at a time (Jira project `PROT`). The first slice is
**authentication** (PROT-7): a user authenticates to the registry as an identified GitHub
user so their synced skills are tagged to them.

## Architecture

- **ASP.NET Core minimal API** (.NET 10), `src/Protostar.Registry.Api`.
- **Postgres + EF Core** for storage. (A graph store such as ArcadeDB is deferred to the
  lineage/refinement epic, where the skill DAG actually needs one.)
- **.NET Aspire** orchestrates local dev (`src/Protostar.Registry.AppHost`): it brings up
  Postgres and the API together with a dashboard, OpenTelemetry, health checks, and
  resilience defaults (`src/Protostar.Registry.ServiceDefaults`).
- **Auth = OpenIddict, federated to GitHub.** OpenIddict is the registry's own OAuth2/OIDC
  authorization server: it owns the `User` records and mints the tokens the CLI stores.
  The actual login step is delegated to GitHub, so the registry never handles passwords.
  The CLI authenticates with the Authorization Code flow + PKCE over a loopback redirect
  (RFC 8252).

### Endpoints

| Endpoint | Purpose |
|---|---|
| `/connect/authorize` | OAuth2 authorization (challenges GitHub if the user isn't signed in) |
| `/connect/token` | Exchange an authorization code (or refresh token) for tokens |
| `/connect/userinfo` | The identified user behind an access token |
| `/connect/logout` | End the session |
| `/.well-known/openid-configuration` | OIDC discovery document |
| `/v1/meta` | Service version + supported API majors (the compatibility contract) |

## Versioning and CLI compatibility

The registry and the CLI version **independently** — each repo drives its own version from
git tags via [MinVer](https://github.com/adamralph/minver) and ships releases via
[release-please](https://github.com/googleapis/release-please). They are not lockstepped.

Compatibility is instead an **API contract**: the HTTP API is served under `/v1`, and
`GET /v1/meta` advertises the API majors the running registry supports
(`{ "apiMajors": [1] }`). The CLI targets an API major and checks it on connect, failing
with an upgrade hint on mismatch. A breaking wire change bumps the API major (a new `/v2`
group); the registry supports the current and previous major during a transition window.

## Local development

Requires the .NET 10 SDK and a container runtime (Docker/Podman) for Postgres.

The API is served over HTTPS (OpenIddict rejects plain HTTP), so trust the ASP.NET Core dev
certificate once:

```bash
dotnet dev-certs https --trust
```

```bash
# GitHub OAuth app (one-time): create one at https://github.com/settings/developers
# with callback URL https://localhost:<api-https-port>/signin-github, then:
cd src/Protostar.Registry.AppHost
dotnet user-secrets set "Parameters:GitHubClientId" "<client-id>"
dotnet user-secrets set "Parameters:GitHubClientSecret" "<client-secret>"

# Bring up Postgres + the API + the Aspire dashboard:
dotnet run --project src/Protostar.Registry.AppHost
```

The dashboard URL is printed on startup. The API applies EF Core migrations and seeds the
`protostar-cli` public client automatically in Development.

```bash
dotnet build      # build the solution
dotnet test       # run the integration tests
```

## Releasing

Same model as the CLI repo, but the artifact is a **container image** rather than a binary.

- **stable** — [Conventional Commits](https://www.conventionalcommits.org) drive a
  release-please Release PR; merging it tags `vX.Y.Z` and the workflow builds and pushes the
  image to GHCR as `:vX.Y.Z` and `:latest`. MinVer reads the tag and stamps it into the image.
- **edge** — every code change to `main` rebuilds and pushes a rolling `:edge` image,
  versioned by MinVer (`0.X.Y-alpha.0.N`). The `edge` tag does not start with `v`, so MinVer
  ignores it and the channels never collide.

While pre-1.0, `bump-minor-pre-major` keeps breaking changes in the `0.x` range; cutting
`1.0.0` will be a deliberate choice.

## Repository layout

```text
protostar-registry/
├─ src/
│  ├─ Protostar.Registry.Api/             # ASP.NET Core minimal API: OpenIddict + GitHub + EF Core
│  ├─ Protostar.Registry.AppHost/         # Aspire orchestrator (Postgres + api)
│  └─ Protostar.Registry.ServiceDefaults/ # Aspire OTel / health / resilience defaults
├─ test/
│  └─ Protostar.Registry.Tests/           # integration tests (WebApplicationFactory)
├─ .github/workflows/
│  ├─ ci.yml                # build + test on PRs
│  ├─ release-please.yml    # stable: Release PR -> tag -> build + push GHCR image
│  └─ edge.yml              # edge: rebuild tip of main -> push rolling :edge image
├─ release-please-config.json
├─ .release-please-manifest.json
├─ Directory.Build.props    # MinVer git-tag versioning
└─ protostar-registry.sln
```
