# 0002. Project identity across machines

- **Status:** accepted 2026-11-19 (spike voidprojectssoftware/protostar-cli#44; owner decisions answered in
  voidprojectssoftware/protostar-registry#29)
- **Date:** 2026-11-19
- **Issue:** voidprojectssoftware/protostar-cli#44. Informs protostar-cli#18 and #46 (6.2), and the skill
  collision found in protostar-cli PR #49.

## Context

Claude Code files sessions under `<config root>/projects/<encoded path>/`, and its docs say auto memory
lives in `<config root>/projects/<project>/memory/`, where `<project>` "is derived from the git
repository, so all worktrees and subdirectories within the same repo share one auto memory directory",
and outside a git repo "the project root is used". Either way the key is an absolute local path. The
same repo is `C:\Users\Blake\source\repos\voidprojectssoftware\protostar` on the owner's Windows machine
and `/home/blake/source/repos/void-projects/protostar` on Linux; both folders exist side by side in this
machine's `~/.claude/projects` today. Sessions, memory and search need one identity for "the same
project" that survives different paths, operating systems and worktrees, and a way back to a local path.

What the code knows today (checked, not recalled):

- The CLI's locator (`ClaudeCodeHarness.Sessions.cs`) enumerates `projects/*/` and never interprets the
  folder name. It sends a transcript's `SourcePath`, machine id and metadata.
- The registry learns the working directory and branch only from the transcript's own lines
  (`ClaudeCodeTranscriptParser` reads `cwd` and `gitBranch`). No git facts reach it: no commit, no
  remote, no repo root.
- "Project" in listings is the last segment of the working directory (`TranscriptResponse.ProjectName`),
  and the `--project` filter is `ILIKE '%name%'` on the working directory (`TranscriptService.ListAsync`).
  So `protostar` on Windows and Linux match by luck, and two unrelated folders named `website` would merge.

## Decision

**A project is a registry entity with an opaque id. A git checkout is attached to it by its root
commit. Everything else is display or a fallback that the user can link.**

1. **Identity key for a git checkout: the first-parent root commit of the default branch.** The CLI
   computes `git rev-list --first-parent --max-parents=0 <default branch>`, where the default branch is
   the first of: the remote HEAD (`refs/remotes/<remote>/HEAD`, `origin` preferred), local `main`, local
   `master`, then `HEAD`. The commit hash is content-addressed, so it is the same on every machine, path
   and operating system, and the same in every worktree.
2. **Display name: the normalised remote URL**, from `origin` or else the first remote: scheme, user
   and `.git` dropped, `git@host:owner/repo` rewritten to `host/owner/repo`, host lowercased. Display
   only, latest seen wins. With no remote, the main worktree's folder name.
3. **Worktrees fold into their repo.** All worktrees share one object store
   (`git rev-parse --git-common-dir`), so they yield the same key. The main worktree's path (the parent of
   the common dir) is what memory is filed under, matching Claude Code's own rule.
4. **No usable key yet: the project is keyed by machine plus path until linked.** A non-git folder, a
   working directory that no longer exists, or a shallow clone the registry cannot resolve becomes a
   project keyed `path:<machine id>:<path>`. It is a real project with a real id, so sessions, memory and
   search all work for it from the start. The user can link two projects into one (for example the
   Windows and Linux `protostar` workspace folders), after which every key of either resolves to the
   merged project.
5. **The CLI gathers facts, the registry decides.** Only the CLI can run git in the checkout, so it sends
   the raw facts with each registration: working directory, whether it is a git repo, shallow or not,
   the computed key, the full list of roots reachable from `HEAD`, the default branch used, remote names
   and URLs, the repo top level, the main worktree path, and the path within the repo
   (`git rev-parse --show-prefix`). The registry normalises remotes, maps keys to project ids and
   applies links. Because the registry keeps the facts, it can **recompute** the mapping whenever this
   rule changes, without the CLI. The git facts themselves can only be refreshed by the CLI sending them
   again; registering a transcript is already idempotent (`PUT /v1/transcripts/{harness}/{key}`), so a
   newer CLI can re-send facts for transcripts backed up before this existed, as long as the checkout is
   still on disk.
6. **Never decode Claude Code's folder names.** The encoding replaces every character that is not a
   letter or digit with `-`, so it is not reversible (see evidence E6). Paths come from a transcript's
   `cwd` or from git; when the CLI needs a folder name it encodes forward from a known path and checks
   that the folder exists.

### How each consumer uses it

All three resolve a local working directory to a project id the same way: compute the facts in that
directory, ask the registry (or a cached mapping) for the project id.

- **Sessions.** Each transcript is attached to the project of its `cwd` on its machine. Listings show the
  display name and filter by project id, replacing the last-segment name and the `ILIKE` filter. The path
  within the repo is kept per session, so a monorepo subfolder can still be shown and filtered.
- **Memory.** Upload: the CLI maps a `projects/<folder>/memory/` folder to a checkout by encoding forward
  the main worktree paths it knows (from transcripts' `cwd` in that folder, or from checkouts it found)
  and files the memory under that checkout's project id. Pull (6.2): for each project, find this
  machine's checkouts of it, encode forward each main worktree path, and write into that folder under
  the harness's config root (`--harness-home`). A project with no checkout here is skipped and reported.
- **Search.** Every indexed document carries the project id. "This project" means the project id of the
  current working directory, computed as above.
- **Project skills** (the collision in protostar-cli PR #49). Yes, project identity should key project
  skills: a project skill becomes (project id, name), a global skill stays (creator, name). Then a
  global and a project skill with the same name, or two projects' same-named skills, never map to one
  registry skill.

### The awkward cases

| Case | Answer |
| --- | --- |
| Same repo at different paths or on different OSes | Same root commit, so same project (E1). Paths, drive letters and folder-name case play no part. |
| Several worktrees | Same root commit and same common dir, so same project (E2). Memory goes to the main worktree's folder. |
| More than one remote, or a renamed remote | The key ignores remotes, so nothing changes (E4). Only the display name may change; latest seen wins. A repo renamed or moved between owners on GitHub stays the same project too. |
| A fork | Shares its upstream's root commit, so the same project by default (E4). **Owner decision 1.** |
| A folder that is not a git repo | Keyed by machine plus path until the user links it (E7). The owner's own Linux `protostar` workspace is such a folder. |
| A monorepo subfolder opened on its own | Same project as the repo, with the path within the repo recorded per session (E7). This matches Claude Code, which shares one memory folder across a repo's subdirectories. |
| More than one root commit | Only the first-parent root of the default branch is the key; the others are recorded but never matched on (E3). |
| Shallow clone | Its "root" is the shallow boundary, which is wrong (E5). The CLI detects `--is-shallow-repository` and sends no key; the registry resolves it by a remote it already knows from a full clone, or keys it by machine plus path until a full clone or `git fetch --unshallow`. |
| Working directory deleted (agent worktrees) | Walk up to the nearest existing ancestor; if that is a git repo, use it, otherwise machine plus path (E7). |
| `CLAUDE_CONFIG_DIR` or a profile tool moves the config root | Identity does not depend on the config root at all, only on the transcript's `cwd`. Memory pull writes under whichever root the harness uses on that machine. |

### Traps found, and how the rule handles them

- **"The root commit" is not one commit.** Merging unrelated histories or `git subtree add` without
  `--squash` gives `HEAD` two roots, and `git subtree add --squash` adds a root too (E3). Orphan
  branches add roots that `HEAD` only reaches when they are checked out: in the owner's repos
  `reinvent-hallway` has 27 roots across all refs, `voidprojects-site` 3 and `void-crm` 2, all from
  screenshot branches. Hence the first parent of the default branch, not "any root" and not `--all`.
- **Matching on any shared root would be wrong.** A repo that pulled in a library with `git subtree`
  contains the library's root, so "share a root, same project" would merge the app and the library (E3).
- **The first-parent root can change.** Merging the mainline *into* a side branch with a different root
  and then fast-forwarding the mainline to it moves the first-parent root (E3). This is rare. When a
  known checkout (same machine and path) reports a new key whose recorded roots include its old key, the
  registry keeps it on its project instead of creating a new one.
- **Remote HEAD is often missing.** `void-crm` and `reinvent-hallway` have no `refs/remotes/origin/HEAD`
  (E2), presumably because they were created with `git init` and a push rather than a clone. Hence the
  `main`, `master`, `HEAD` fallbacks.
- **Identical root commits in unrelated repos** happen only when two first commits are byte-identical
  (same tree, author, committer, timestamps and message), for example from a script with fixed dates.
  Projects are per owner, so this can only merge one user's own repos, and an unlink would fix it.

## Options considered

- **Normalised remote URL as the identity (strongest alternative). Rejected.** It is the best display
  name, and it is what a shallow clone can offer, which is why it is kept as both. As the identity it
  breaks when a repo is renamed or moved between owners (this owner already has repos under both
  `BlakeHastings` and `voidprojectssoftware`), it is ambiguous with several remotes (a fork clone has
  `origin` and `upstream`, and E4 shows a rename to `mine`), it needs normalisation across HTTPS and SSH
  forms, and a local-only repo has none.
- **A marker file committed to the repo. Rejected.** It has to be committed to every repo, including
  client repos; it is missing from clones and branches made before it was added; and it is copied by
  anything that copies files. `void-crm` began as "copy b-fac 0.64.0 assets in", so a marker in `b-fac`
  would have made `void-crm` the same project. Its root commit is different (E1).
- **A user-assigned name with a per-machine path map. Rejected as the primary.** It asks the user to do
  something on every machine for every repo. It survives as the link for non-git folders.
- **The path, or Claude Code's encoded folder name. Rejected.** Different on every machine by
  definition, and the folder name cannot even be decoded back to a path (E6).
- **Root commit of `HEAD` as the issue proposed. Accepted with the refinements above**: first-parent,
  default branch rather than `HEAD`, never trusted in a shallow clone, and attached to an opaque project
  id so links and recomputation are possible.

## Owner decisions

Both answered by the owner on 2026-11-19 ("agreed.", accepting both recommendations below; recorded in
voidprojectssoftware/protostar-registry#29).

1. **Is a fork the same project as its upstream?** Decided: yes, by default. The code is the same,
   so memory about it applies to both; the root commit cannot tell them apart, and only the remote URL
   (rejected above as unstable) could. A hard fork that becomes a separate product can be split later.
2. **May the registry link non-git folders automatically?** For example the Windows and Linux
   `protostar` workspace folders, which share a last segment. Decided: no automatic link; suggest
   it ("`protostar` on Windows looks like `protostar` on Linux, link them?") and let the user confirm,
   because a wrong merge mixes memory between unrelated folders.
3. **Added by the owner:** a user can link a folder to a project themselves, and can see which project
   Protostar perceives the current directory to be in (protostar-cli#57).

## Assumptions

- The Claude Code memory location rule is taken from its docs (code.claude.com/docs/en/memory), fetched
  2026-11-19. Observation agrees: the three `b-fac` agent-worktree folders in `~/.claude/projects` have no
  `memory/`, while the `b-fac` folder does. The docs also mention an `autoMemoryDirectory` setting that
  moves memory elsewhere; 6.2 must honour it.
- The folder-name encoding was checked on this machine's names only: all 331 match `[A-Za-z0-9-]`, the
  longest is 119 characters. Behaviour for very long paths was not observed.
- A partial clone (`--filter=blob:none`) keeps full commit history, so its root is correct. The local
  test server ignored the filter, so this rests on git's documented behaviour, not on E5.

## Evidence

Outputs trimmed. Hashes shortened to 12 characters where marked.

**E1. Root commits are content-addressed and repo-specific.** The registry's root, locally and on GitHub:

```
$ git -C protostar-registry rev-list --max-parents=0 HEAD
59dc947b0dba097d8d1dca3121154d3839dc3983
$ git cat-file -p 59dc947b0dba        # no path or machine in the object
tree e6110adf9cfad22a7032af9a88d8eae888a27d33
author Blake Hastings <email> 1780376298 -0500
committer Blake Hastings <email> 1780376298 -0500
$ gh api repos/voidprojectssoftware/protostar-registry/commits/59dc947b0dba... -q '.sha, (.parents|length)'
59dc947b0dba097d8d1dca3121154d3839dc3983 0 parents
```

Template copy versus original: `b-fac` root `f3b8a7a41c41`, `void-crm` root `05b9feaa618f`.

**E2. Worktrees share the root and the common dir** (every repo under `/home/blake/source/repos`, 48
checkouts, 16 of them linked worktrees including this one; a selection):

```
checkout                               type   root(12)      git-common-dir
protostar-cli                          main   4a08ea85ed81  protostar-cli/.git
protostar-cli-15                       wt     4a08ea85ed81  protostar-cli/.git
protostar-registry                     main   59dc947b0dba  protostar-registry/.git
protostar-registry-adr                 wt     59dc947b0dba  protostar-registry/.git
homelab-services / -omnigent / -runners       5b99879ae103  homelab-services/.git
void-crm / -deploy / -live / -wt-prov         05b9feaa618f  void-crm/.git
voidprojects-site-infra-acs            wt     (none)        fatal: not a git repository  (its main repo's worktree record is gone)

$ git -C void-crm symbolic-ref refs/remotes/origin/HEAD
fatal: ref refs/remotes/origin/HEAD is not a symbolic ref
$ git -C protostar-cli symbolic-ref refs/remotes/origin/HEAD
refs/remotes/origin/main
```

No local checkout was shallow (`--is-shallow-repository` false for all 47 readable ones).

**E3. More than one root** (throwaway repos under `/tmp/protostar-spike-44`):

```
# merged unrelated histories
$ git rev-list --max-parents=0 HEAD
4bfc16bf0117...   root A
6286e4cc89d8...   root B (unrelated history)
$ git rev-list --max-parents=0 --reverse --date-order HEAD | head -1
6286e4cc89d8...                # order is not a stable tie-breaker
$ git rev-list --first-parent --max-parents=0 HEAD
4bfc16bf0117...                # the mainline's root

# git subtree add --squash
$ git log --format='%h | %s' --max-parents=0 HEAD
2e43643 | app root
ce0b479 | Squashed 'vendor/lib/' content from commit e2155f6
# git subtree add (full history): the library's own root is now in the app
df0cbb6 | app2 root
e2155f6 | lib root
$ git rev-list --first-parent --max-parents=0 HEAD
df0cbb6

# worktree on an orphan branch
$ git -C tworoots-wt rev-list --first-parent --max-parents=0 HEAD    -> 6286e4c
$ git -C tworoots-wt rev-list --first-parent --max-parents=0 main    -> 4bfc16b

# the first-parent root can move: merge main INTO side, then fast-forward main
$ git log --format='%h %s' --first-parent --max-parents=0 HEAD
b509e7a root B (side)

# real orphan branches
$ git -C void-crm rev-list --first-parent --max-parents=0 pr-screenshots   -> 22747b2c712d
reinvent-hallway: HEAD roots 1, --all roots 27; voidprojects-site 1 vs 3; void-crm 1 vs 2
```

**E4. Fork, extra remote, renamed remote:**

```
$ git -C forkclone remote -v      # clone of a bare "fork" of a bare "upstream"
origin    /tmp/protostar-spike-44/fork.git (fetch)
upstream  file:///tmp/protostar-spike-44/upstream.git (fetch)
$ git rev-list --first-parent --max-parents=0 HEAD    -> 59dc947b0dba
$ git remote rename origin mine
$ git rev-list --first-parent --max-parents=0 HEAD    -> 59dc947b0dba
$ git symbolic-ref refs/remotes/mine/HEAD             -> refs/remotes/mine/main
```

**E5. Shallow clone:**

```
$ git clone --depth 1 file://.../protostar-registry shallow
$ git -C shallow rev-parse --is-shallow-repository    -> true
$ git -C shallow rev-list --max-parents=0 HEAD
e9f7ed1ea4f9eb2f761e7cf7a90f8baba61c0588             # the tip, not the root: no error, just wrong
$ cat shallow/.git/shallow
e9f7ed1ea4f9eb2f761e7cf7a90f8baba61c0588
$ git -C shallow fetch --unshallow
$ git -C shallow rev-list --max-parents=0 HEAD
59dc947b0dba097d8d1dca3121154d3839dc3983
```

**E6. Claude Code's folder names cannot be decoded** (names listed only; no transcript was opened):

```
-home-blake-source-repos-void-projects-protostar   <= /home/blake/source/repos/void-projects/protostar   (real)
-home-blake-source-repos-void-projects-protostar   <= /home/blake/source/repos/void/projects/protostar
-home-blake-source-repos-void-projects-protostar   <= /home/blake/source/repos/void.projects/protostar
-home-blake-source-repos-b-fac--claude-worktrees-agent-a4a122d33bb743f94   <= .../b-fac/.claude/worktrees/agent-a4a1...
C--Users-Blake-source-repos-voidprojectssoftware-protostar                 <= C:\Users\Blake\source\repos\voidprojectssoftware\protostar
$ ls ~/.claude/projects | grep -c '[^A-Za-z0-9-]'     -> 0   (of 331)
```

**E7. Non-git folders, subfolders, deleted working directories:**

```
$ git -C /home/blake/source/repos/void-projects/protostar rev-parse --show-toplevel
fatal: not a git repository        # the owner's protostar workspace holds 5 repos and is not one
$ cd protostar-cli/src && git rev-parse --show-toplevel --show-prefix
/home/blake/source/repos/void-projects/protostar/protostar-cli
src/
$ git rev-list --first-parent --max-parents=0 HEAD    -> 4a08ea85ed81   (same as the repo root)
$ ls -d /home/blake/source/repos/b-fac/.claude/worktrees/agent-*
ls: cannot access ...: No such file or directory   # yet their folders remain in ~/.claude/projects
```
