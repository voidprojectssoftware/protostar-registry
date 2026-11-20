# Definition of Done and the review process

An issue is done when it passes **three lenses**. Mechanical checks are not one
of the lenses: they are the price of admission, they run in CI, and no human or
agent should spend judgment on them.

## Gate 0: mechanical (automated, no judgment)

CI runs these. If they are red, the work is not ready for review.

- build: `dotnet build protostar-registry.sln`
- unit tests (domain only, no host or database): `dotnet test test/Protostar.Registry.Tests`
- acceptance scenarios (the real API over Postgres and RustFS in Docker): `dotnet test test/Protostar.Registry.Acceptance`
- EF migrations compile with the model (a missing migration shows as a pending model change, which the acceptance migrations scenario fails on)

Never ask a reviewer to run these by hand. If a mechanical check is missing,
adding it is cheaper than reviewing for it forever.

## Lens 1: functionality, proven by interaction

**The reviewer drives the running app.** Not the tests, the app.

```bash
export PATH=$HOME/.dotnet:$PATH DOTNET_ROOT=$HOME/.dotnet
cd src/Protostar.Registry.AppHost && dotnet run   # Postgres (+ object store) in Docker, API on https://localhost:7443
# sign in without a browser: protostar auth login --provider dev --no-browser, then curl -sk -L the printed URL
# exercise the endpoints with the CLI or curl; stop the AppHost and the Protostar.Registry.Api process after
```

Run the test suites first: what they cover, you do not have to re-check.

Then exercise **the change itself** as the actual user would, since no suite
covers what landed today. Confirm:

- the happy path works end to end
- one realistic failure path behaves sanely (bad input, expired link, network drop)
- no console errors, no unhandled promise rejections
- no new error-level logs or failed spans

"Tests pass" is not evidence of functionality. A green suite over an app that
does not load is a common and embarrassing outcome. Say what you actually did
and what you actually saw.

## Lens 2: code quality, proven by comprehension

**The reviewer must be able to explain what the code does without asking the
author.** If they cannot, that is the finding. Unclear code is a defect even
when it is correct.

Specifically reject:

- code whose shape mimics a pattern elsewhere without the reason that motivated it
- abstractions with exactly one caller and no second caller in sight
- names that restate the type (`dataObject`, `handleThing`, `utils`)
- comments explaining *what* a line does rather than *why* it is that way
- swallowed errors, `any`, and suppression directives without an adjacent reason
- defensive code for conditions that cannot occur

Prefer deleting code to adding a flag. The best review outcome is a smaller diff.

## Lens 3: architecture, proven by entropy accounting

Every change either uses an existing pattern or introduces a new one.
**Introducing a new pattern is a decision that must be named and justified**,
not something that happens quietly in a feature PR.

Ask on every change:

1. Does this duplicate something we already have? Search before adding.
2. Does it add a dependency? What did we get, and what does it cost to remove later?
3. Does it put logic in a new layer or a new place? Why is the existing place wrong?
4. Would a new engineer find this where they would look for it?
5. Is the project's single source of truth still single, or did a parallel one
   just get born?

If a change introduces a new pattern deliberately, record it in
`docs/architecture/decisions/` as a short ADR. Three sentences is a fine ADR.
The point is that the decision is findable later, not that it is ceremonious.

Take the next number after everything on the default branch, everything in an
open pull request, **and** everything in a worktree (`git worktree list`), where
a running agent's unpushed ADR is invisible to the other two. Work runs in
parallel here, so the next free number on your branch is usually already claimed
on someone else's.
There is no collision check yet; the orchestrator hands out ADR numbers in the brief.

## Recording the review

Post the outcome on the issue or PR with these headings. Be specific and honest:

```
## Functionality
What I ran, what I clicked, what I saw. Include failures found.

## Code
What this code does, in my own words. Concerns, if any.

## Architecture
New patterns introduced, dependencies added, duplication found.

## Verdict
Ship / Ship with follow-ups (linked) / Needs work (specific changes)
```

An empty section is a signal the lens was skipped. Say "not applicable, this is
a docs-only change" rather than leaving it blank.

## This process is itself reviewable

If a gate is producing ceremony instead of signal, **change it**. Open an issue
against this document, say which gate wasted effort and what it should be
instead, and edit it. A review process nobody believes in is worse than none,
because it launders unreviewed work as reviewed.

Bias: automate anything mechanical, keep human and agent judgment for the three
lenses, and delete any step that has never once caught a real problem.
