# Capabilities

`GET /v1/meta` is public and answers with the registry's version, the API majors it serves, and its
**capabilities**: stable names for the features added within API v1 that this build has.

```json
{
  "service": "protostar-registry",
  "version": "0.2.0",
  "apiMajors": [1],
  "capabilities": ["skills", "stale-skill-push", "project-skills", "skill-rename", "skill-delete", "skill-tombstones",
                   "sessions", "transcript-lookup", "transcript-prefix-lookup", "session-skills", "redacted-read",
                   "projects", "memory", "memory-content", "memory-base-versions", "search"]
}
```

The API major says whether a client and a registry share a wire contract at all. A capability says whether
a registry that shares it has a particular feature. A shared registry can lag behind the CLI by a release
or more, so a client checks for a name before it uses that feature. When the name is missing, the client
says "this registry does not support memory yet; upgrade it" instead of failing on a 404.

## The rule

- A name is added in the same change that ships its feature.
- A name is never removed or renamed within API v1. A feature that goes away is a breaking change, and
  that means a new API major.
- Names are lowercase kebab-case, and are compared exactly.
- A client ignores names it does not know.

The names are defined in one place, `src/Protostar.Registry.Api/ApiCapabilities.cs`, with a doc comment
on each saying what a client may rely on. A unit test checks that every name is unique and kebab-case.
The acceptance feature `Capabilities.feature` pins the list and uses each feature once, so a name cannot
be added or dropped unnoticed, and cannot stay listed after its feature is gone.

## The names

| Name | What a client may rely on when it is listed |
|---|---|
| `skills` | Push global skills one at a time or in bulk (`POST /v1/skills`, `/v1/skills/bulk`), list them with a content hash each (`GET /v1/skills`), and download a version (`GET /v1/skills/{name}/files`). |
| `stale-skill-push` | A push may send `baseVersion`. If that version is no longer current and the content differs, the push is refused with `409` and problem type `stale-skill-version`, carrying `currentVersion`. |
| `project-skills` | Skills may belong to a project. A push sends the folder's facts in `project`, and reads take `projectId` or `includeProjects`. Project skills follow a folder when it is linked or unlinked. |
| `skill-rename` | `POST /v1/skills/{name}/rename` renames a skill in place, keeping its versions. |
| `skill-delete` | `DELETE /v1/skills/{name}` deletes a skill with every version. |
| `skill-tombstones` | Deleted and renamed names are remembered. The list carries `removed` and each skill's `firstVersion`, and a push from a copy of a removed skill is refused with `409` and problem type `skill-deleted` or `skill-renamed`. |
| `sessions` | Register machines (`/v1/machines`), register transcripts and append to them chunk by chunk, download them, read their parsed summaries, and list them. |
| `transcript-lookup` | `POST /v1/transcripts/lookup` returns many transcripts' stored positions in one request. |
| `transcript-prefix-lookup` | `GET /v1/transcripts?keyPrefix=7d3c&limit=11` returns the caller's transcripts whose key starts with `keyPrefix`, ignoring case, up to `limit`. A prefix shorter than 4 characters, or with a character no key can hold, is refused with `400`. |
| `session-skills` | `GET /v1/transcripts/{harness}/{key}/skills` lists the skills the transcript invoked (a session's list includes its subagents'), each with `name`, `uses`, `firstUsedAt` and `lastUsedAt`. A name that matched one of the caller's registry skills (personal first, then the session's project, as Claude Code loads them) carries `skillId` and, when a version had been pushed by the last use, the inferred `version` with `versionInferred: true`. `deleted: true` marks a skill since deleted. Plugin skills (`plugin:skill`) match nothing. |
| `redacted-read` | A transcript download takes `redacted=true` and returns the content with known secrets replaced by markers. Every JSON line is still JSON. |
| `projects` | Sessions are filed under projects shared across machines (ADR 0002). A folder can be resolved (`POST /v1/projects/resolve`), linked (`/link`) and unlinked (`/unlink`). |
| `memory` | Push a project's changed and deleted memory files (`POST /v1/memory`), and list projects and files (`GET /v1/memory`, `/v1/memory/{projectId}`). |
| `memory-content` | `GET /v1/memory/{projectId}/content` downloads a project's memory files with their bytes, for pull. |
| `memory-base-versions` | A pushed memory file may carry `baseVersion`, which is kept as the new version's base. Content downloads carry each file's `history` of versions and bases, and `deletedOn`, the copies that deleted it. |
| `search` | `GET /v1/search` searches the caller's sessions and memory, with secrets redacted. |

## Adding a capability

1. Add a `const` to `ApiCapabilities` with a doc comment saying what a client may rely on, and add it to
   `ApiCapabilities.All`.
2. Add its row to the table above.
3. In `test/Protostar.Registry.Acceptance/Features/Capabilities.feature`, add the name to both tables,
   and add a check that uses the feature to `CapabilitySteps`.
