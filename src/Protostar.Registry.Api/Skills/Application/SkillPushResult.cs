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

    /// <summary>A file path was absolute, rooted, empty, or contained a <c>..</c> traversal segment.</summary>
    InvalidPath,

    /// <summary>No <c>SKILL.md</c> was present at the skill root.</summary>
    MissingSkillManifest,

    /// <summary>The SKILL.md declared no name and the pusher supplied none.</summary>
    MissingName,

    /// <summary>The authenticated creator no longer exists in the registry.</summary>
    UnknownCreator,

    /// <summary>The caller is not the skill's creator, so may not push a new version of it.</summary>
    NotCreator,

    /// <summary>The skill's content exceeds the size limits the registry can store.</summary>
    TooLarge,
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
    public SkillPushError Error { get; init; } = SkillPushError.None;
    public string? Message { get; init; }

    public static SkillPushResult Created(string name, int version, Guid skillId, Guid versionId, int fileCount) =>
        new()
        {
            Status = SkillPushStatus.Created,
            Name = name,
            Version = version,
            SkillId = skillId,
            VersionId = versionId,
            FileCount = fileCount,
        };

    public static SkillPushResult Unchanged(string name, int version, Guid skillId, Guid versionId, int fileCount) =>
        new()
        {
            Status = SkillPushStatus.Unchanged,
            Name = name,
            Version = version,
            SkillId = skillId,
            VersionId = versionId,
            FileCount = fileCount,
        };

    public static SkillPushResult Failed(string? name, SkillPushError error, string message) =>
        new() { Status = SkillPushStatus.Failed, Name = name, Error = error, Message = message };
}
