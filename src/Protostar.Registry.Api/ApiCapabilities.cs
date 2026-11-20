namespace Protostar.Registry.Api;

/// <summary>The features this registry build serves within API v1, advertised by name in <c>/v1/meta</c>'s <c>capabilities</c>.</summary>
/// <remarks>
/// <para>
/// The API major says which wire contract a client and registry share; a capability says which features added
/// within that major a registry has. A client checks for a name before using its feature, so a registry that lags
/// behind the CLI is met with "this registry does not support memory yet; upgrade it" rather than a bare 404.
/// </para>
/// <para>
/// The rule (AGENTS.md): a name is added in the same change that ships its feature, and is never removed or renamed
/// within v1. Names are stable, lowercase kebab-case. Each name's summary is what a client may rely on when the name
/// is present. <c>docs/api/capabilities.md</c> lists them for readers who do not read C#.
/// </para>
/// </remarks>
internal static class ApiCapabilities
{
    /// <summary>
    /// Global skills: push one or many (<c>POST /v1/skills</c>, <c>/v1/skills/bulk</c>), list them
    /// (<c>GET /v1/skills</c>) with a content hash per skill, and download a version (<c>GET /v1/skills/{name}/files</c>).
    /// </summary>
    public const string Skills = "skills";

    /// <summary>
    /// A skill push may send <c>baseVersion</c>; one based on a version that is no longer current, with different
    /// content, is refused with <c>409</c> and problem type <c>stale-skill-version</c> carrying <c>currentVersion</c>.
    /// </summary>
    public const string StaleSkillPush = "stale-skill-push";

    /// <summary>
    /// Skills may belong to a project: a push sends the folder's facts in <c>project</c>, and reads take
    /// <c>projectId</c> or <c>includeProjects</c>. Project skills follow a folder when it is linked or unlinked.
    /// </summary>
    public const string ProjectSkills = "project-skills";

    /// <summary>A skill can be renamed in place, keeping its versions: <c>POST /v1/skills/{name}/rename</c>.</summary>
    public const string SkillRename = "skill-rename";

    /// <summary>A skill can be deleted with every version: <c>DELETE /v1/skills/{name}</c>.</summary>
    public const string SkillDelete = "skill-delete";

    /// <summary>
    /// Deleted and renamed skill names are remembered: the skill list carries <c>removed</c> and each skill's
    /// <c>firstVersion</c>, and a push from a copy of a removed skill is refused with <c>409</c> and problem type
    /// <c>skill-deleted</c> or <c>skill-renamed</c>.
    /// </summary>
    public const string SkillTombstones = "skill-tombstones";

    /// <summary>
    /// Session backup: register machines (<c>/v1/machines</c>), register transcripts and append to them chunk by chunk,
    /// download them, read their parsed summaries, and list them.
    /// </summary>
    public const string Sessions = "sessions";

    /// <summary>Many transcripts' stored positions in one request: <c>POST /v1/transcripts/lookup</c>.</summary>
    public const string TranscriptLookup = "transcript-lookup";

    /// <summary>
    /// The transcript list takes <c>keyPrefix</c> (at least 4 characters) and returns the caller's transcripts whose key
    /// starts with it, ignoring case, up to <c>limit</c>; a shorter or malformed prefix is refused with <c>400</c>.
    /// </summary>
    public const string TranscriptPrefixLookup = "transcript-prefix-lookup";

    /// <summary>
    /// The skills a transcript used, with a session's subagents' included: <c>GET /v1/transcripts/{harness}/{key}/skills</c>.
    /// Each has <c>name</c>, <c>uses</c>, <c>firstUsedAt</c>, <c>lastUsedAt</c>, and, when it matched a registry skill,
    /// <c>skillId</c> and an inferred <c>version</c> (<c>versionInferred</c>); <c>deleted</c> marks a skill since deleted.
    /// </summary>
    public const string SessionSkills = "session-skills";

    /// <summary>
    /// A transcript download takes <c>redacted=true</c> and returns the content with known secrets replaced by markers,
    /// every JSON line still JSON.
    /// </summary>
    public const string RedactedRead = "redacted-read";

    /// <summary>
    /// Projects shared across machines (ADR 0002): sessions are filed under a project, and a folder can be resolved
    /// (<c>POST /v1/projects/resolve</c>), linked (<c>/link</c>) and unlinked (<c>/unlink</c>).
    /// </summary>
    public const string Projects = "projects";

    /// <summary>Memory backup: push a project's changed and deleted memory files (<c>POST /v1/memory</c>) and list what is kept.</summary>
    public const string Memory = "memory";

    /// <summary>A project's memory files can be downloaded with their bytes, for pull: <c>GET /v1/memory/{projectId}/content</c>.</summary>
    public const string MemoryContent = "memory-content";

    /// <summary>
    /// A pushed memory file may carry <c>baseVersion</c>, kept as the new version's base; content downloads carry each
    /// file's <c>history</c> of versions and bases, and <c>deletedOn</c>, the copies that deleted it.
    /// </summary>
    public const string MemoryBaseVersions = "memory-base-versions";

    /// <summary>Full-text search over the caller's sessions and memory, with secrets redacted: <c>GET /v1/search</c>.</summary>
    public const string Search = "search";

    /// <summary>
    /// A memory content download takes <c>redacted=true</c> and returns each file's content with known secrets replaced
    /// by markers, for reading memory as a person or an agent should.
    /// </summary>
    public const string MemoryRedactedRead = "memory-redacted-read";

    /// <summary>Every capability this build serves, in the order <c>/v1/meta</c> lists them.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        Skills, StaleSkillPush, ProjectSkills, SkillRename, SkillDelete, SkillTombstones,
        Sessions, TranscriptLookup, TranscriptPrefixLookup, SessionSkills, RedactedRead,
        Projects,
        Memory, MemoryContent, MemoryBaseVersions,
        Search,
        MemoryRedactedRead,
    ];
}
