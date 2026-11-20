# Running the registry in production

This is the contract between the registry image and whatever runs it. The reference deployment is
[`docker-compose.example.yml`](docker-compose.example.yml): the API, Postgres 18 and RustFS 1.0.5 (the same
versions the Aspire AppHost runs in development), with TLS terminated at a reverse proxy in front.

```
browser / CLI --https--> reverse proxy (TLS, e.g. Nginx Proxy Manager) --http--> api:8080 --> postgres:5432
                                                                                          \-> objectstore:9000 (S3)
```

## The image

`ghcr.io/voidprojectssoftware/protostar-registry`, `linux/amd64`, built from the [`Dockerfile`](../../Dockerfile)
at the repository root.

| Tag | Pushed when | Moves? |
|---|---|---|
| `:vX.Y.Z` | release-please creates release `vX.Y.Z` (`release-please.yml`) | no |
| `:latest` | the same release | yes, to the newest release |
| `:edge` | every code change merged to `main` (`edge.yml`) | yes, to the tip of `main` |
| `:sha-<short>` | the same push to `main`, `<short>` being the first 7 characters of the commit | no |

Pin a deployment to `:vX.Y.Z` (or `:sha-<short>` to run something unreleased); `:latest` and `:edge` are for
trying things. The running version is `GET /v1/meta`'s `version`.

The image runs as the base image's non-root `app` user (UID 1654), listens on plain HTTP port 8080, defaults to
`ASPNETCORE_ENVIRONMENT=Production`, and has a `HEALTHCHECK` on `/health`.

## Environment variables

ASP.NET Core maps `__` in a variable name to a configuration section (`ObjectStore__Bucket` is
`ObjectStore:Bucket`). "Required" means the registry refuses to start, or cannot do its job, without it.

### Host

| Variable | Required | Meaning | Example |
|---|---|---|---|
| `ASPNETCORE_ENVIRONMENT` | yes (image default) | Must be `Production`. Anything but `Development` requires the token certificates below and never offers the dev sign-in. | `Production` |
| `ASPNETCORE_HTTP_PORTS` | no (image default `8080`) | The plain-HTTP port inside the container. Publish it on the host and point the proxy at it. | `8080` |
| `ASPNETCORE_URLS` | no | Overrides `ASPNETCORE_HTTP_PORTS` when set. Leave it unset unless you need to bind a specific address. | `http://+:8080` |

### Database

| Variable | Required | Meaning | Example |
|---|---|---|---|
| `ConnectionStrings__registrydb` | yes | Npgsql connection string for Postgres. The database must exist; the registry creates and migrates its tables. Add `GSS Encryption Mode=Disable`: the image has no Kerberos library, and Npgsql logs a load error when it tries GSS without one. | `Host=postgres;Port=5432;Database=registry;Username=registry;Password=...;GSS Encryption Mode=Disable` |

### Object store (S3-compatible, see ADR 0001)

| Variable | Required | Meaning | Example |
|---|---|---|---|
| `ObjectStore__ServiceUrl` | yes, unless AWS S3 | The store's base URL. Empty means AWS S3 itself, chosen by `ObjectStore__Region`. | `http://objectstore:9000` |
| `ObjectStore__AccessKey` | yes | Access key. For RustFS, the `RUSTFS_ACCESS_KEY` it was started with. | 24 random characters |
| `ObjectStore__SecretKey` | yes | Secret key. For RustFS, `RUSTFS_SECRET_KEY`. | 48 random characters |
| `ObjectStore__Bucket` | no (default `protostar-registry`) | Bucket for transcript content. Created on first use if missing. It must belong to this database alone (the orphan sweep deletes blobs this database does not reference). | `protostar-registry` |
| `ObjectStore__Region` | no (default `us-east-1`) | Signing region. Irrelevant to RustFS. | `us-east-1` |
| `ObjectStore__OrphanSweepEnabled` | no (default `true`) | Whether this instance sweeps orphaned transcript chunks. Leave on. | `true` |
| `ObjectStore__OrphanSweepInterval` | no (default `06:00:00`) | How often the sweep runs. | `06:00:00` |
| `ObjectStore__OrphanSweepGrace` | no (default `1.00:00:00`) | How old an unreferenced blob must be before it is deleted. | `1.00:00:00` |

### Reprocessing

When a release improves how the registry reads sessions (a parser or redaction fix), stored sessions are reread
from the object store so listings, reads and search improve without anyone re-uploading or upgrading the CLI. A
pass runs at startup and then hourly; a pass with nothing to do is a few cheap queries. To run one on demand and
exit, without serving: `dotnet Protostar.Registry.Api.dll reparse-sessions` (`reindex-search` is the same pass).

| Variable | Required | Meaning | Example |
|---|---|---|---|
| `Reprocessing__Enabled` | no (default `true`) | Whether this instance runs the pass in the background. Leave on. | `true` |
| `Reprocessing__Interval` | no (default `01:00:00`) | Time between passes. | `01:00:00` |
| `Reprocessing__BatchSize` | no (default `20`) | Transcripts per batch. Memory does not depend on it: one chunk (at most 16 MiB) is read at a time. | `20` |
| `Reprocessing__BatchPause` | no (default `00:00:02`) | Pause after each full batch, so a large backlog does not hold the database and store continuously. | `00:00:02` |

### GitHub sign-in

| Variable | Required | Meaning | Example |
|---|---|---|---|
| `GitHub__ClientId` | yes | Client id of the GitHub OAuth app for this deployment. Without it the registry boots but nobody can sign in. | `Ov23li...` |
| `GitHub__ClientSecret` | yes | That app's client secret. | `...` |

The GitHub OAuth app's **Authorization callback URL** is:

```
https://<public host>/signin-github
```

The path is the GitHub handler's default `CallbackPath` (`GitHubAuthenticationDefaults.CallbackPath` in
`AspNet.Security.OAuth.GitHub`; the registry does not override it, see `SignInRegistration`). The handler builds
the full URL from the request's scheme and host, which are the public ones once forwarded headers apply (below).
Use a separate OAuth app from the development one, whose callback is `https://localhost:7443/signin-github`.

### Token signing and encryption keys

| Variable | Required | Meaning | Example |
|---|---|---|---|
| `OpenIddict__SigningCertificate__Path` | yes | PFX file with the key that signs access and identity tokens, mounted read-only. | `/token-certs/signing.pfx` |
| `OpenIddict__SigningCertificate__Password` | yes (unless the PFX has none) | Its password. | from `passwords.env` |
| `OpenIddict__EncryptionCertificate__Path` | yes | PFX file with the key that encrypts authorization codes and refresh tokens. | `/token-certs/encryption.pfx` |
| `OpenIddict__EncryptionCertificate__Password` | yes (unless the PFX has none) | Its password. | from `passwords.env` |
| `OpenIddict__Issuer` | no, recommended | Pins the OIDC issuer instead of deriving it from each request. Set it to the public origin. | `https://registry.example.com` |

Without both certificate paths a non-Development registry exits at startup with a message naming these
variables. A missing file, wrong password, or certificate without its private key also stops it, naming the
setting. It never falls back to development keys.

### Reverse proxy

| Variable | Required | Meaning | Example |
|---|---|---|---|
| `ForwardedHeaders__KnownProxies` | yes behind a proxy (this or the next) | Comma-separated IP addresses of the proxy, as the API sees the connection. | `192.0.2.10` |
| `ForwardedHeaders__KnownNetworks` | alternative to the above | Comma-separated CIDR ranges whose members are trusted as proxies. Prefer exact addresses. | `192.0.2.0/28` |

An entry that is not an address or range stops startup. Loopback is always trusted.

### Optional

| Variable | Meaning | Example |
|---|---|---|
| `OTEL_EXPORTER_OTLP_ENDPOINT` | Send logs, traces and metrics over OTLP (Aspire service defaults). Unset means no export. | `http://otel-collector:4317` |
| `Logging__LogLevel__Default` | Log level. EF Core logs every SQL command at `Information`; `Logging__LogLevel__Microsoft.EntityFrameworkCore.Database.Command=Warning` quiets it. | `Information` |

## Signing and encryption keys

**Choice: PFX files mounted into the container, passwords from the environment.** Not base64 PFX in an
environment variable, because:

- The keys must outlive the container (tokens are kept by every CLI across registry restarts and upgrades), and
  a file on the host, backed up with the deployment, makes that the obvious state of affairs.
- Environment variables are visible to anyone who can `docker inspect` the container and tend to end up in
  logs and shell history. A 3 KB base64 blob is also awkward in a `.env` file. The password alone in the
  environment is low value without the file.
- A read-only bind mount works the same with Docker secrets or any secret manager that writes files.

Generate them once per deployment:

```bash
scripts/generate-token-certificates.sh ./token-certs
sudo chown 1654 ./token-certs/*.pfx     # the container's user must be able to read them (they are mode 600)
```

It writes `signing.pfx` (key usage `digitalSignature`) and `encryption.pfx` (`keyEncipherment`), self-signed RSA
3072, valid ten years, and `passwords.env` with the two random passwords as the `OpenIddict__*__Password`
variables. It refuses to overwrite existing files. Self-signed is correct: OpenIddict uses only the key pair and
publishes the public signing key at `/.well-known/jwks`; nobody validates a chain.

**Back up `token-certs/` and the passwords.** Losing or replacing either key signs every user out: access
tokens stop validating and refresh tokens cannot be decrypted, so each CLI has to `protostar auth login` again.
Rotation without that (OpenIddict accepts several certificates and signs with the newest) is not built yet.
OpenIddict refuses an expired certificate, so plan to replace them before the ten years are up.

## Behind the reverse proxy

The API serves plain HTTP and OpenIddict refuses any request it believes is plain HTTP, so it must see the
request as the proxy received it:

1. The proxy terminates TLS for `<public host>` and forwards to `http://<docker host>:<published port>`.
2. It sends `X-Forwarded-Proto: https` and `X-Forwarded-For`, and either passes the original `Host` header or
   sends `X-Forwarded-Host`. Nginx Proxy Manager does this by default for a proxy host with scheme `http`.
3. `ForwardedHeaders__KnownProxies` holds the proxy's address as the API sees it. With a published port and the
   proxy on another machine, that is the proxy machine's IP (Docker's port forwarding keeps the source address).
   Headers from any other peer are ignored, so nobody can claim a different scheme or host by sending them.

Then the discovery document, the issuer, the GitHub callback and all redirects use `https://<public host>`.
Setting `OpenIddict__Issuer` as well pins the issuer even for a request that reaches the API another way.

The CLI's sign-in keeps working through the proxy: the `protostar-cli` client is a public client with loopback
redirect URIs (`http://127.0.0.1/callback`, `http://localhost/callback`, any port, RFC 8252). The browser is
redirected to `127.0.0.1` on the user's own machine, which the proxy never sees. The proxy must not rewrite
`Location` headers (Nginx Proxy Manager does not).

**Symptoms of a missing or wrong known proxy:** `/.well-known/openid-configuration` answers `400` with
`This server only accepts HTTPS requests` (ID2083), or shows `http://` URLs.

## Migrations

**The registry applies pending EF Core migrations at startup**, in every environment, before it starts serving
or runs its background services, so the client seeder, the orphan sweep and reprocessing always see the current schema. There
is no separate migration step to forget, and an image upgrade migrates itself.

It is safe with one instance, which is how the registry runs: nothing else writes the schema. It is also safe if
two instances ever start together: EF Core takes `LOCK TABLE "__EFMigrationsHistory" IN ACCESS EXCLUSIVE MODE`
for the duration, so the second waits and then finds nothing to apply. A migration that fails stops startup with
the error, and the container restarts and retries.

On an empty database the first start logs one `fail` for `relation "__EFMigrationsHistory" does not exist`: that
is EF Core checking for the table before creating it, not an error.

Back up the database before upgrading to a release whose notes mention a migration; migrations only go forward.

## Data that must persist

| What | Where | If lost |
|---|---|---|
| Postgres data | volume at `/var/lib/postgresql` (Postgres 18 keeps it in `18/docker` below that) | everything |
| Object store data | RustFS volume at `/data` | all transcript content |
| Token keys | `token-certs/` on the host, mounted read-only | every user signed out |
| Data Protection keys | volume at `/home/app/.aspnet/DataProtection-Keys` | browser sign-in sessions and in-flight GitHub sign-ins (not CLI tokens) |

## Health

`/health` answers `Healthy` (200) when the app and its Postgres connection are up, `Unhealthy` (503)
otherwise; `/alive` checks only the process. Both return just the status word. The image's `HEALTHCHECK`
probes `/health` every 30 seconds.

## Checking a deployment

```bash
curl -s https://<public host>/v1/meta                              # version, API majors and capabilities
curl -s https://<public host>/health                               # Healthy
curl -s https://<public host>/.well-known/openid-configuration     # issuer and endpoints are https://<public host>/...
protostar auth login --registry https://<public host>              # GitHub sign-in end to end
```

`capabilities` in that answer names the features this build serves (see
[docs/api/capabilities.md](../api/capabilities.md)). A CLI newer than the registry checks it before using a feature, and
asks for a registry upgrade when the name it needs is missing: upgrading the image is the fix.

Then restart the API container and run `protostar auth status` (or any command that calls the registry): the
token from before the restart must still work.
