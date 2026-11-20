using Protostar.Registry.Api.Projects;

namespace Protostar.Registry.Api.Skills;

/// <summary>The outcome of pushing one skill.</summary>
public enum SkillPushStatus
{
    /// <summary>A new version was stored.</summary>
    Created,

    /// <summary>The push matched the current version byte-for-byte; no new version was stored.</summary>
    Unchanged,

    /// <summary>The skill could not be stored; see <see cref="SkillPushResult.Error"/>.</summary>
    Failed,
}

/// <summary>Why a push failed, so a caller can map it to the right status code or message.</summary>
public enum SkillPushError
{
    None,

    /// <summary>The upload carried no files.</summary>
    NoFiles,

    /// <summary>A file path failed <see cref="RelativePath.TryCreate"/>, or two paths differ only by case.</summary>
    InvalidPath,

    /// <summary>No <c>SKILL.md</c> was present at the skill root.</summary>
    MissingSkillManifest,

    /// <summary>The SKILL.md declared no name and the pusher supplied none.</summary>
    MissingName,

    /// <summary>The skill's name contains a path separator (see <see cref="Skill.IsValidName"/>).</summary>
    InvalidName,

    /// <summary>The authenticated creator no longer exists in the registry.</summary>
    UnknownCreator,

    /// <summary>The caller is not the skill's creator, so may not push a new version of it.</summary>
    NotCreator,

    /// <summary>The skill's content exceeds the size limits the registry can store.</summary>
    TooLarge,

    /// <summary>The project facts sent with a project skill are malformed.</summary>
    InvalidProject,

    /// <summary>
    /// The push was based on an older version than the current one: the skill changed on another machine first.
    /// <see cref="SkillPushResult.CurrentVersion"/> says which version is current.
    /// </summary>
    StaleVersion,

    /// <summary>
    /// The push came from a copy of a skill that was deleted or renamed in the registry since that copy synced
    /// (<see cref="SkillTombstone.Refuses"/>). <see cref="SkillPushResult.LastVersion"/> is that skill's last version
    /// under this name, and <see cref="SkillPushResult.RenamedTo"/> its name now when it was renamed.
    /// </summary>
    Removed,
}

/// <summary>
/// The result of ingesting one skill: a success carrying the stored version, an idempotent no-op, or a
/// typed failure. Presentation-free so the single and bulk endpoints can each shape their own response.
/// </summary>
public sealed record SkillPushResult
{
    public required SkillPushStatus Status { get; init; }
    public string? Name { get; init; }
    public int? Version { get; init; }
    public Guid? SkillId { get; init; }
    public Guid? VersionId { get; init; }
    public int FileCount { get; init; }

    /// <summary>The project a project skill was filed under; <c>null</c> for a global skill or a failure.</summary>
    public ProjectResolution? Project { get; init; }

    /// <summary>The skill's current version when a push was refused as <see cref="SkillPushError.StaleVersion"/>; <c>null</c> otherwise.</summary>
    public int? CurrentVersion { get; init; }

    /// <summary>The removed skill's last version under this name, for <see cref="SkillPushError.Removed"/>; <c>null</c> otherwise.</summary>
    public int? LastVersion { get; init; }

    /// <summary>The removed skill's name now, for a <see cref="SkillPushError.Removed"/> rename; <c>null</c> otherwise.</summary>
    public string? RenamedTo { get; init; }

    public SkillPushError Error { get; init; } = SkillPushError.None;
    public string? Message { get; init; }

    public static SkillPushResult Created(string name, int version, Guid skillId, Guid versionId, int fileCount, ProjectResolution? project = null) =>
        new()
        {
            Status = SkillPushStatus.Created,
            Name = name,
            Version = version,
            SkillId = skillId,
            VersionId = versionId,
            FileCount = fileCount,
            Project = project,
        };

    public static SkillPushResult Unchanged(string name, int version, Guid skillId, Guid versionId, int fileCount, ProjectResolution? project = null) =>
        new()
        {
            Status = SkillPushStatus.Unchanged,
            Name = name,
            Version = version,
            SkillId = skillId,
            VersionId = versionId,
            FileCount = fileCount,
            Project = project,
        };

    public static SkillPushResult Stale(string name, int currentVersion, string message, ProjectResolution? project = null) =>
        new()
        {
            Status = SkillPushStatus.Failed,
            Name = name,
            Error = SkillPushError.StaleVersion,
            CurrentVersion = currentVersion,
            Message = message,
            Project = project,
        };

    public static SkillPushResult Removed(SkillTombstone tombstone, int baseVersion, ProjectResolution? project = null) =>
        new()
        {
            Status = SkillPushStatus.Failed,
            Name = tombstone.Name,
            Error = SkillPushError.Removed,
            LastVersion = tombstone.LastVersion,
            RenamedTo = tombstone.RenamedTo,
            Message = tombstone.RenamedTo is { } renamedTo
                ? $"This copy was based on version {baseVersion} of '{tombstone.Name}', which was renamed to '{renamedTo}' in the registry at version {tombstone.LastVersion}. Nothing was stored."
                : $"This copy was based on version {baseVersion} of '{tombstone.Name}', which was deleted from the registry at version {tombstone.LastVersion}. Nothing was stored.",
            Project = project,
        };

    public static SkillPushResult Failed(string? name, SkillPushError error, string message) =>
        new() { Status = SkillPushStatus.Failed, Name = name, Error = error, Message = message };
}
