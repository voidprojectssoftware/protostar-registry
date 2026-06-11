---
sidebar_position: 1
slug: /
title: Protostar Registry
---

:::warning AI-generated and not yet reviewed
This page was drafted by AI and has not been reviewed by a human. Protostar is in
early development with limited maintainer bandwidth, so content may be incomplete
or inaccurate. Treat it as a starting point, verify anything important against the
source, and please report any problems you hit.
:::

# Protostar Registry

The **registry** is the service side of Protostar — the service that identified
users will sync their skills to, and that the refinement loop runs against. The
[CLI](/cli) is a separate, disconnected client; the registry is the API and
datastore it talks to.

Protostar's loop is **use → sync → refine → suggest → adopt → use.** The registry
is where synced skills will live, get tagged to a user, and route suggestions back.

:::info What works today
The registry's first slice is **authentication**: a user signs in as an
identified GitHub user (see [Concepts](./concepts.md)). **Skill sync, lineage, and
refinement are not built yet** — there is no `protostar sync` command, and nothing
is stored against a user beyond their identity. The pages here document what runs
today and flag what is coming.
:::

## What the registry is responsible for

- **Identity.** It is its own OAuth2/OIDC authorization server (built on
  OpenIddict) that owns the `User` records and mints the tokens the CLI stores.
  The actual login is federated to GitHub, so the registry never handles
  passwords.
- **A versioned API.** The HTTP surface is served under `/v1`, and a small
  metadata endpoint advertises which API majors the running registry supports —
  the contract the CLI checks before it does anything. See
  [Concepts](./concepts.md).
- **Storage.** Postgres via EF Core today. (A graph store for skill lineage is
  deferred to the refinement epic, where the skill DAG actually needs one.)

## Who these docs are for

The registry is a backend service, so these pages are aimed at the people who
**run, operate, or develop** it rather than end users — end users only ever touch
the [CLI](/cli).

- **Want to understand how it fits together?** Read [Concepts](./concepts.md).
- **Want to run it locally and sign in end to end?** Follow
  [Getting started](./getting-started.md).

The registry is built incrementally, one component at a time, alongside the rest
of Protostar.
