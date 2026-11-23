# 0003. Custom agents are a sibling module, not a second kind of skill

- **Status:** Accepted 2026-11-23 (owner decision 1 decided by the orchestrator on PR #85)
- **Date:** 2026-11-23
- **Issue:** #84 (spike 7.0 of epic voidprojectssoftware/protostar-cli#37). Decides the shape of protostar-cli#124
  (7.1), #125 (7.2) and #126 (7.3), and notes what protostar-cli#34 (E4) inherits.

## Context

A Claude Code custom agent is one Markdown file with YAML front matter, in `~/.claude/agents/` (personal) or a
project's `.claude/agents/`. Skills already have the full sync machinery: personal and project keying (ADR 0002),
versions with a base version and stale refusal (#55), tombstones for renames and deletes (#59), the CLI ledger, the
capture hook, `protostar setup`, and the skills a session used (#76). E7 wants the same behaviour for agents
(cli#124 assumes "agents behave exactly as skills do in every respect the skills work already settled").

The question: does an agent become a second kind of the `Skill` aggregate (a kind discriminator, one push, pull
and ledger path), or a sibling module that copies the pattern?

What Claude Code does with agents, from its documentation (code.claude.com/docs/en/sub-agents, fetched
2026-11-23), where it differs from skills:

- **Identity is the `name` field, not the file.** "The filename doesn't have to match." `name` and `description`
  are required. A skill is a folder named after it.
- **The folders are scanned recursively,** so an agent may sit at `agents/review/reviewer.md`.
- **Project beats personal.** On a name clash the project agent wins (managed, then `--agents`, then
  `.claude/agents/`, then `~/.claude/agents/`, then plugins). For skills the personal one wins, and
  `SessionSkills.Match` encodes that order.
- **The front matter is a different schema:** `tools`, `disallowedTools`, `model`, `permissionMode`, `skills`,
  `mcpServers`, `hooks`, `memory`, `color` and more, none of which is in the Agent Skills standard that
  `SkillManifest` holds (`license`, `compatibility`, `metadata`, `allowed-tools`).
- **Used through a different call:** an `Agent` (formerly `Task`) tool call whose `input.subagent_type` names the
  agent. Built-in types (`general-purpose`, `Explore`, `Plan`, `claude`, `statusline-setup`, `claude-code-guide`,
  `web-fetch`) are not custom agents.

### How skill-specific `Skills/` is (measured)

Registry `src/Protostar.Registry.Api/Skills/`, 26 files, 2,201 lines (`wc -l` on `main` at ebdae9d):

| Bucket | Types | Lines |
| --- | --- | --- |
| **Carries over unchanged** (apart from the noun) | `Skill` (create, rename, delete check, `PushVersion` rules), `SkillTombstone`, `SkillCreatorPolicy`, the four exceptions, `SkillVersionPushed`, `SkillVersionPushOutcome`, `SkillChangeService`, `SkillQueryService`, `SkillPushResult`, `SkillTombstoneConfiguration`, `Sha256Hash`, `FolderSkillMove` | 966 (44%) |
| **Needs a kind switch** | `SkillVersion` (its columns are the skill standard's fields), `SkillManifest` and `SkillManifestParser`, `SkillSizePolicy` (5 MiB per file, 20 MiB per version: sized for bundles), `SkillPushService` (finds `SKILL.md`, falls back to the folder name), `SkillConfiguration` (unique index, files table), `SkillEndpoints` (multipart file parts, descriptions) | 1,096 (50%) |
| **Folder-only** | `SkillFile`, `RelativePath`, `SkillFileContent`, `SkillFileUpload`, and the bulk endpoint inside `SkillEndpoints` (grouped by folder segment) | 139 (6%) |

One correction to the issue's framing: **`FolderSkillMove` is not about skill folders.** It is the rule for which
project skills follow a *project folder* (`<machine id>:<path>`) when that folder is linked or unlinked (#39). It
applies to project agents exactly as it does to project skills, so it is a shared rule, not a skill-only one.

Outside `Skills/`, skills reach into three places: `ProjectService` (link and unlink move skills and refuse on a
name clash; forgetting an empty folder project checks `_db.Skills`), `TranscriptService.GetSkillsAsync` with
`SessionSkills` and `SkillUse` (the skills a session used), and `RegistryDbContext`. In all, 20 queries over
`Skills` or `SkillTombstones` sit in 5 files.

CLI `src/Protostar.Cli/` (protostar-cli `main` at b1c88bf): discovery (`ISkillCapability`,
`ClaudeCodeHarness.Skills`, `ClaudeCodeSkillParser`, `DiscoveredSkill`), `SkillFileReader` and `SkillInstaller`
are folder-only: they walk a directory, require `SKILL.md`, prune and rename folders. `SkillChangeService` and
the pull's rename-follow rewrite the `SKILL.md` name and move the folder. The ledger (`SkillLedger`, keyed by full
path), `MovedSkillLedger`, the presenters and the push and pull classification are generic in logic but typed to
skills throughout. The capture hook acts only on `PostToolUse` for the `Skill` tool.

A precedent exists: memory (protostar-cli#45) is single-file sync and was built as a sibling module (`Memory/`),
with its own aggregate, its own versioning rules (content-addressed versions, per-machine copies) and endpoints
under `/v1/memory`, reusing `Sha256Hash` and `RelativePath` from `Skills/`. It did not become a skill kind.

## Decision

**Agents are a sibling feature module, `Agents/`, in both repos, built in the skills module's image.** They get
their own aggregate, tables, endpoints, capabilities, CLI services and ledger. They share only what is already
shared or genuinely generic: `Sha256Hash`, `RelativePath`, project resolution (`ProjectService.ResolveAsync`), the
front-matter fence reader (moved out of `SkillManifestParser`, which would be its second caller) and the
folder-move rule (generalised over both). The versioning core (base version, stale refusal, tombstones, the
unique-index race) is copied, not abstracted, until a third caller exists.

### Registry

- **Domain:** `Agent` (creator, optional project, `CarriedByFolderKey`, name, current version), `AgentVersion`
  (number, `Description`, `ToolsJson`, `Model`, the file's `Path` relative to the agents folder, its bytes
  verbatim, size, `ContentHash`, pusher, time), `AgentTombstone`, `AgentManifest` and `AgentManifestParser`
  (`name` and `description` required, so a missing `name` is refused rather than defaulted from the file name),
  `AgentSizePolicy` (one file; a limit sized for a prompt, not a bundle), `AgentVersionPushed`.
- **Tables (new, nothing existing changes):** `Agents` with a unique `(CreatorId, ProjectId, Name)` index, nulls
  not distinct, as skills; `AgentVersions` with a unique `(AgentId, VersionNumber)`; `AgentTombstones` keyed as
  skill tombstones. No files table: the one file lives on the version row.
- **Endpoints, under `/v1/agents`:** `POST` (multipart with exactly one file part, plus the `project` and
  `baseVersion` fields the skill push takes, so the CLI's multipart code is reused; 409 problems for stale, deleted
  and renamed, with the same field names as skills), `GET` (list: global, `includeProjects`, `projectId`, and
  `removed`), `GET /{name}/content` (one version's bytes and metadata), `POST /{name}/rename`, `DELETE /{name}`. No
  bulk endpoint: it exists for skills only because a skill is a folder of parts.
- **Projects:** link and unlink move project agents with project skills in one save, through the folder-move rule
  generalised from `FolderSkillMove`; a clash is checked per module (an agent and a skill may share a name), and
  `ProjectLinkResponse` gains `movedAgents`. `ForgetFolderProjectsAsync` also keeps a project that has agents.
- **Capabilities** (`ApiCapabilities`, added by the story that ships each): `agents` (push with base version and
  removed refusals, project agents, list, rename, delete), `agent-content` (download), `session-agents`,
  `agent-search`.
- **Sessions:** the transcript parser also counts `Agent` and `Task` tool calls by `input.subagent_type` into a new
  `TranscriptSummary.Agents` list (shaped like `SkillUse`), and `GET /v1/transcripts/{harness}/{key}/agents`
  matches them at read time with **project first, then personal**, Claude Code's order for agents. Only names that
  match one of the owner's agents or agent tombstones are returned, so built-in and plugin (`plugin:agent`) types
  never appear as custom agents.
- **Search:** a new `SearchSource.Agent`, indexed from the current version on `AgentVersionPushed` (redacted, like
  every entry), removed on delete, and filled for existing agents by the backfill. `kind=agent` filters to it.

### CLI

- **Harness:** `IAgentCapability` with `DiscoverAgents`, `GlobalAgentsDirectory` and `ProjectAgentsDirectory`;
  `ClaudeCodeHarness.Agents` scans `agents/` recursively for `*.md` and reads `name` and `description`;
  `DiscoveredAgent` carries name, scope, file path and project root.
- **`Agents/`:** `AgentPushService`, `AgentPullService`, an `AgentLedger` (`~/.protostar/agent-ledger.json`, keyed
  by full file path, same record shape and refusal memory as `SkillLedgerEntry`), the moved-ledger rule after a
  link, `AgentInstaller` (verify hash, write one file atomically, never outside the agents folder), presenters.
- **Commands:** an `agents` branch (`push`, `remote`, `pull`), with `rename` and `delete` when a story asks for them.
- **Hook:** at `SessionEnd`, the capture hook hashes the agent files of the session's `cwd` (and personal ones)
  against the ledger in process, with no network call when nothing changed, and hands any changed ones to a
  detached push, as it already does with the transcript. `install-hooks` changes nothing: `SessionEnd` is already
  installed.
- **Setup:** a new `SetupStep.Agents` after skills, push then pull, same outcome rules.
- **MCP:** `search` accepts `kind: agent` (gated on `agent-search`) and a read-only `get_agent` returns a
  definition (gated on `agent-content`). Installing stays a CLI action (cli ADR 0001).

## Consequences

- **No migration of live data.** Registry v0.1.0 is released and a homelab deployment is pending. This option
  only adds tables (`Agents`, `AgentVersions`, `AgentTombstones`), a nullable `AgentId` on the search entries and a
  jsonb `Agents` column on the transcript summary defaulting to empty. No skill row, index or column changes.
- **One reprocessing pass.** Recording agents used means bumping `TranscriptSummary.CurrentParserVersion` from 2
  to 3, so `TranscriptReprocessor` re-reads every stored transcript from the object store once after the upgrade.
  This cost is the same under either option, because session skills and session agents both come from the summary.
- **Old clients cannot see agents.** A v0.1.0 CLI lists and pulls `/v1/skills`, which stays exactly as it is. With
  a kind column, every one of the 20 existing skill queries would need a kind filter, and one missed filter would
  hand an agent to an old `skills pull` that then fails on a missing `SKILL.md`.
- **Duplication, deliberately.** About 1,000 lines of registry logic (the carries-over bucket) and the generic half
  of the CLI push, pull and ledger are copied with the noun changed. A bug fixed in one copy must be fixed in the
  other; reviewers of either module should check its sibling. Two copies of a rule set this young are cheaper
  than an abstraction designed from one example.
- **Collision surface for 7.1 to 7.3.** The new folders collide with nothing. The shared files are listed per story
  on cli#124, cli#125 and cli#126: `ProjectService` and `FolderSkillMove` (7.1), `ApiCapabilities` and its table
  (each story adds a name), `Program.cs` in both repos, `RegistryDbContext` and the migrations snapshot (7.1 and
  7.3), `TranscriptSummary` and the parser (7.3), `SetupService` (7.2), `CaptureService` (7.1).

### What E4 inherits

E4 (harness configuration, cli#34) is unrefined and is not designed here. It inherits a precedent, not a
mechanism: a single-file definition is its own module with its own nouns under `/v1/<noun>`, its own capability
names and its own CLI ledger, storing the file on the version row. E4 differs from agents more than agents differ
from skills (per harness and per machine, previews before apply, secrets held back per the open owner questions on
cli#34), which is the strongest argument against forcing any of them into the skill aggregate. When E4 becomes the
third module with base versions, stale refusal and tombstones, extract that core from the two working copies,
with three callers to shape it. That extraction belongs to E4's first story, not to E7.

## Options considered

- **A sibling module that copies the pattern. Chosen,** for the reasons above: no change to live tables, old
  clients isolated by construction, the domain's own words, and room for agents' different identity, precedence,
  schema and use signal.
- **A second kind of the skill aggregate (`Kind` on `Skills` and `SkillTombstones`, `/v1/skills?kind=agent`).
  Rejected.** What it would take: a `Kind` column defaulting to `skill` on both tables and the two unique indexes
  rebuilt as `(CreatorId, Kind, ProjectId, Name)` (cheap on today's row counts, but a change to live tables);
  agent fields as more nullable columns on `SkillVersion` beside the skill standard's; `PushVersion` taking a
  manifest that is either kind; a kind filter on all 20 queries; and on the CLI a kind switch through discovery,
  reader, installer, rename and pull, half of whose code is folder-only. It saves the duplicated 44% but spreads a
  switch through the other 56%, puts agents under the noun "skill" in code and API, and makes the precedence and
  built-in rules in `SessionSkills` conditional. Its one real gain, a single push and pull path, is the generic
  half, which E4 is better placed to extract.
- **A generic "definition" core now, with skills and agents as two users. Rejected for now.** It is the
  extraction above, done with two callers and a migration of the live skill tables onto it. It would be designed
  around the folder-shaped case and one single-file case, before E4 shows what a third needs.

## Assumptions

- Claude Code's agent rules (recursive scan, `name` as identity, project-over-personal precedence, the built-in
  list) are from its documentation fetched on 2026-11-23; no local agent files existed to observe (`~/.claude/agents`
  is absent on the dev machine). Whether a custom agent named like a built-in (`Explore`) overrides it was not
  checked; matching against the registry handles either way, since a name only matches an agent the owner pushed.
- An agent whose front matter lacks `name` is refused at push, not named after its file: Claude Code itself would
  not load it.
- Plugin agents are not backed up, as plugin skills are not (the epic puts plugins out of scope).
- Only Claude Code is covered, per cli#124. `IAgentCapability` leaves room for another harness.

## Owner decisions

1. **Renaming and deleting agents from the CLI.** cli#124 assumes rename and delete tombstones, but no story had
   an `agents rename` or `agents delete` command. Decided by the orchestrator on 2026-11-23 (PR #85): the registry
   rename and delete endpoints are built in 7.1 (voidprojectssoftware/protostar-cli#124), and the two CLI commands,
   mirroring `skills rename` and `skills delete`, are filed as voidprojectssoftware/protostar-cli#127 (7.4).
2. **Project agents and git** (the cli#78 item 2 question, already noted on cli#125). Not decided here: 7.2
   follows whatever project skills do when it is built.
