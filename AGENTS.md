# AGENTS.md

Guidance for AI coding agents working in protostar-registry. This is the canonical, tool-agnostic source
of conventions, read by Codex, Cursor, Copilot, Gemini CLI, and others. Claude Code reads it through a
one-line `CLAUDE.md` that imports this file. Human-facing onboarding lives in `README.md`.

## Project

protostar-registry is the registry side of protostar: the service identified users sync their skills to,
and that the refinement loop runs against. It is an ASP.NET Core minimal API (.NET 10) backed by Postgres
via EF Core, with OpenIddict (federated to GitHub) as its OAuth2/OIDC server, orchestrated locally by .NET
Aspire.

- `src/Protostar.Registry.Api/` — the API.
- `src/Protostar.Registry.AppHost/` — Aspire orchestrator (Postgres + the API).
- `src/Protostar.Registry.ServiceDefaults/` — shared Aspire OTel / health / resilience defaults.
- `test/Protostar.Registry.Tests/` — unit tests asserting domain behavior in isolation (value objects, policies, aggregate behavior); no host, no database.

## Build and test

```bash
dotnet build protostar-registry.sln                                  # build everything
dotnet test test/Protostar.Registry.Tests/...                        # unit tests
```

These are fast, isolated unit tests: they assert the domain model's behavior directly (value objects,
policies, aggregate behavior) with no web host, no `WebApplicationFactory`, and no database. They are
authored contract-first, derived from each type's documented contract rather than its implementation.
End-to-end, user-focused scenarios (the full push path through HTTP and Postgres) belong in the BDD
acceptance layer, tracked separately; reach for a real Postgres (e.g. Testcontainers) there, not here.

EF Core migrations:

```bash
dotnet ef migrations add <Name> --project src/Protostar.Registry.Api --startup-project src/Protostar.Registry.Api
```

`dotnet ef migrations remove` connects to the database to check whether a migration was applied; for an
unapplied migration, delete its two files and `git checkout` the `RegistryDbContextModelSnapshot.cs`
instead, then re-add.

## Architecture: this API is Domain-Driven

Think about everything in this API in terms of DDD. The model carries the behavior; services and
endpoints stay thin.

The code is organized **by feature** (vertical slices), not by technical layer, in a single project. A
feature folder holds everything for that feature: its domain model, application service, endpoints, DTOs,
and EF configuration. This was a deliberate choice over the separate-project Clean Architecture template:
for a small, early codebase the only thing separate projects add over folders is compiler-enforced
dependency direction, which is not worth the ceremony yet. If that enforcement is wanted later, add an
architecture test (e.g. NetArchTest) rather than splitting projects; split into projects only when a second
bounded context or a larger team makes it pay off.

```
src/Protostar.Registry.Api/
  Common/          # shared kernel: AggregateRoot, IDomainEvent
  Skills/          # the Skills feature: aggregate + value objects + policy + events + service + endpoints + EF config
  Identity/        # the Identity feature: User, auth/OIDC endpoints, EF config
  Infrastructure/  # cross-cutting persistence: RegistryDbContext (unit of work) + Migrations
```

Each feature folder maps to its own namespace (`Protostar.Registry.Api.Skills`, `.Identity`, `.Common`,
`.Infrastructure`).

Inside a feature, group files by DDD role so the model is easy to navigate. These subfolders are for
navigation only: **everything in a feature keeps the single feature namespace** (e.g. all of `Skills/`
is `Protostar.Registry.Api.Skills`, regardless of subfolder), so there is no intra-feature `using`
churn. The Skills feature is the reference layout:

```
Skills/
  Domain/                         # the aggregate (root + entities), domain services, behavior results
    ValueObjects/                 # RelativePath, Sha256Hash, SkillManifest, ...
    Policies/                     # SkillCreatorPolicy, SkillSizePolicy
    Events/                       # SkillVersionPushed
    Exceptions/                   # domain exceptions
  Application/                    # application service, endpoints, request/response DTOs, result types
  Infrastructure/                 # this feature's EF IEntityTypeConfiguration
```

### DDD conventions

- **Behavior lives on aggregates, not in services.** No anemic models. Entities expose intent-revealing
  methods (e.g. `Skill.PushVersion(...)`); they do not expose open `set` accessors for callers to mutate.
- **Encapsulate state.** Entities use private setters and are created through factory methods
  (`Skill.Create`, `SkillVersion.Create`) or constructors that enforce invariants, so an invalid instance
  cannot be represented. Child entities inside an aggregate carry the foreign key but no back-reference
  navigation; the aggregate root is the only entry point.
- **Aggregate = consistency boundary.** Load it, mutate it through behavior, save it as a unit. Reach other
  aggregates by id, not by navigation.
- **Invariants live in the domain and are enforced at the write boundary.** Express a rule as an aggregate
  method or a domain policy (e.g. `SkillCreatorPolicy`). For a violation that should not happen through the
  normal path, throw a domain exception (e.g. `SkillCreatorViolationException`); the application service
  catches it and translates to a transport result.
- **Value objects over primitive obsession.** Wrap concepts that carry rules or formatting in immutable
  value objects (`RelativePath`, `Sha256Hash`) that validate on construction. Expose a `FromTrusted`
  factory only for the persistence layer to rehydrate already-validated data.
- **Domain events for cross-cutting reactions.** Aggregates raise events (`SkillVersionPushed`) via
  `AggregateRoot`; an application service persists with `SaveChangesAndDispatchAsync`, which dispatches them
  *after commit* (so handlers see persisted facts) to `IDomainEventHandler<T>` implementations resolved from
  DI. React to an event by registering a handler, not by editing the write path. A `LoggingDomainEventHandler`
  is the current placeholder consumer. (Dispatch lives at the call site, not an overridden `SaveChangesAsync`,
  because Aspire pools the context and a pooled context cannot take a scoped dispatcher.)
- **Application services are thin.** They build domain inputs, load or create the aggregate, call its
  behavior, own the transaction, and map the outcome to a result type. They hold no business rules.
- **EF mapping lives with the model.** Each entity gets an `IEntityTypeConfiguration<T>` co-located with it
  in its feature folder (e.g. `SkillConfiguration`, `UserConfiguration`); `RegistryDbContext` only applies
  them via `ApplyConfigurationsFromAssembly`. Value-object columns are mapped with `HasConversion` there.
- **Endpoints are thin.** An endpoint maps the request to a service call and shapes the response; it
  contains no logic.
- **Ubiquitous language.** Use the domain's words in code (push, skill, version, creator). "Creator" is
  provenance, not an ownership or permissions grant.

## C# conventions

- **No em dashes** in any output: code, comments, commit messages, docs. Restructure the sentence instead.
- **Constructors.** Primary constructors are for records (positional) and small lightweight types (DTOs,
  simple wrappers, exceptions). Logic-bearing classes use an explicit `private readonly` field plus a
  constructor, so injected dependencies are `readonly` and there is a place for guard clauses.
- **XML doc comments.** `<summary>` is one short sentence on *what* a thing is; rationale and design intent
  go in `<remarks>`. Document the non-obvious (units, nullability meaning, side effects, ordering), not the
  signature.
