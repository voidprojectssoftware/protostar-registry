# Working an issue

How a change gets from an issue to `main`. Read `review.md` first: it defines what "done" means.
This file is only the mechanics.

## Before you start

Read, in this order:

1. `AGENTS.md` for the conventions and how to build and test
2. `docs/process/review.md` for the three review lenses
3. The issue itself, including its parent epic (epics E1 to E7 are issues #31 to #37 in
   voidprojectssoftware/protostar-cli; stories span both protostar-cli and protostar-registry)

If the issue conflicts with something you find in the code, say so on the issue rather than quietly
picking one. A stale issue is a normal thing to find, and checking the premise is part of the job.

## Temp files and long commands

Two traps that have each cost a builder hours, twice:

- **The scratchpad directory your harness gives you is not yours.** A subagent inherits the
  orchestrator's session scratchpad path, and writing there mixes your files with the orchestrator's.
  Put fixtures, scripts and fake home folders in your worktree's `.scratch/` (add it to
  `.git/info/exclude`) or a `mktemp -d`.
- **Do not feed a heredoc to an interpreter inside a long compound command.** A Bash call of the form
  `python3 - <<'EOF' ... EOF; source lib.sh; ...` has stalled with no child process for over an hour,
  and a heredoc call hung another builder for three hours. Write the script to a file with a file
  tool, then run it, and put `timeout` on anything that waits (a server, a sign-in, a CLI call against a
  registry). Commit work in progress early, so a stall costs minutes, not the branch.

## Branch and commits

```
feat/<issue-number>-<short-slug>      fix/<issue-number>-<short-slug>      docs/... chore/...
```

Conventional commits with the issue in the subject, as the history shows:
`feat(cli): capture hook backs up a skill when it changes (#42)`. release-please reads them, so the
type matters. The body says why, not what.

## Verify before you open the PR

Do not open a PR you have not run.

```bash
export PATH=$HOME/.dotnet:$PATH DOTNET_ROOT=$HOME/.dotnet
cd src/Protostar.Registry.AppHost && dotnet run   # Postgres (+ object store) in Docker, API on https://localhost:7443
# sign in without a browser: protostar auth login --provider dev --no-browser, then curl -sk -L the printed URL
# exercise the endpoints with the CLI or curl; stop the AppHost and the Protostar.Registry.Api process after
```

Agents run in parallel, each in its own worktree, and each worktree already has its own database and
object store. Give each one its own API port too, so nobody queues behind 7443:

- Pick a free port per worktree (`ss -ltn | grep :7543` prints nothing when 7543 is free).
- Run the stack with it: `PROTOSTAR_API_PORT=7543 dotnet run` in `src/Protostar.Registry.AppHost`.
  The API URL is printed once it is ready; the dashboard takes free ports of its own.
- Pass `--registry https://localhost:7543` to the CLI.
- The GitHub sign-in only works on 7443, the port its OAuth callback names. Use the dev provider
  (`--provider dev`) on other ports.
- Never stop a stack you did not start. Find your own processes by your worktree path.

Then exercise the change the way the real user would. Stop anything you started (AppHost, API
processes) before you finish. Disk on the dev VM is limited: `dotnet clean` your worktree after the
last build.

## The pull request

Title: the conventional commit subject. Reference the issue with `Closes #N` (or
`Closes voidprojectssoftware/protostar-cli#N` from the registry). The body is the three lenses, filled
in honestly; see `.github/pull_request_template.md`.

Write every body in a file and post it with `--body-file`, never `--body "..."`, then read back what
was stored. Keep the file in `.bodies/` at the root of your own worktree (ignored by git), never in a
shared scratch directory.

## You do not merge. Ever.

If you are an agent working an issue, these are prohibited, without exception:

- `gh pr merge` in any form
- `git push` to `main`, including `git push origin HEAD:main`
- `git merge` while standing on `main`
- merging through `gh api`

Push your branch, open the PR, report back, and stop. The orchestrator reviews and merges.

## Merge discipline

CI runs 1 check: `build-test`. The `main branches` ruleset requires a squash PR with one approval, but org
admins bypass it, and the factory runs as an org admin, so **GitHub does not enforce green checks
here.** Three things stand in for it:

1. **`node scripts/merge-pr.mjs <n>`**, the only sanctioned way to land a PR. It refuses unless every
   required check is green on a branch that is up to date with `main`, and always squash merges
   (through the org-admin bypass, since nobody else approves). *Not covered:* anyone who does not use it.
2. **`scripts/guard-merge.mjs`**, a PreToolUse hook in `.claude/settings.json`. It denies the
   commands above before they run. *Not covered:* sessions that did not load it.
3. **`scripts/check-main-provenance.mjs`**, run by `.github/workflows/provenance.yml` on every push
   to `main`. It fails when a commit is not from a merged PR, or a merged PR lacks the
   `Landed-by: merge-pr.mjs` trailer. *Not covered:* prevention; it notices afterwards.

If a commit ever reaches `main` outside this path, treat it as a defect in the guard: work out what
it missed, add the case, and say so.

## When the process is the problem

If the same manual check is done on every issue, it belongs in CI. If a rule keeps getting broken by
accident, it needs a check rather than another paragraph. Open an issue and say what you observed.
