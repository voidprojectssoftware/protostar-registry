using Protostar.Registry.Api.Projects;

namespace Protostar.Registry.Api.Agents;

/// <summary>The outcome of pushing one agent.</summary>
public enum AgentPushStatus
{
    /// <summary>A new version was stored.</summary>
    Created,

    /// <summary>The push matched the current version byte-for-byte; no new version was stored.</summary>
    Unchanged,

    /// <summary>The agent could not be stored; see <see cref="AgentPushResult.Error"/>.</summary>
    Failed,
}

/// <summary>Why a push failed, so the endpoint can map it to a status code.</summary>
public enum AgentPushError
{
    None,

    /// <summary>The upload did not carry exactly one file.</summary>
    NotOneFile,

    /// <summary>The file's path failed <see cref="Skills.RelativePath.TryCreate"/>.</summary>
    InvalidPath,

    /// <summary>The front matter is missing, or lacks <c>name</c> or <c>description</c> (<see cref="AgentManifestParser"/>).</summary>
    InvalidManifest,

    /// <summary>The agent's name contains a path separator (<see cref="Agent.IsValidName"/>).</summary>
    InvalidName,

    /// <summary>The authenticated creator no longer exists in the registry.</summary>
    UnknownCreator,

    /// <summary>The caller is not the agent's creator.</summary>
    NotCreator,

    /// <summary>The file exceeds <see cref="AgentSizePolicy"/>.</summary>
    TooLarge,

    /// <summary>The project facts sent with a project agent are malformed.</summary>
    InvalidProject,

    /// <summary>The push was based on an older version than the current one; <see cref="AgentPushResult.CurrentVersion"/> says which is current.</summary>
    StaleVersion,

    /// <summary>
    /// The push came from a copy of an agent deleted or renamed since that copy synced (<see cref="AgentTombstone.Refuses"/>);
    /// <see cref="AgentPushResult.LastVersion"/> and <see cref="AgentPushResult.RenamedTo"/> say which.
    /// </summary>
    Removed,
}

/// <summary>The result of pushing one agent: a stored version, an idempotent no-op, or a typed failure.</summary>
public sealed record AgentPushResult
{
    public required AgentPushStatus Status { get; init; }
    public string? Name { get; init; }
    public int? Version { get; init; }
    public Guid? AgentId { get; init; }
    public Guid? VersionId { get; init; }

    /// <summary>The path the current version was pushed from, relative to the agents folder.</summary>
    public string? Path { get; init; }

    /// <summary>The project a project agent was filed under; <c>null</c> for a personal agent or a failure.</summary>
    public ProjectResolution? Project { get; init; }

    /// <summary>The agent's current version for <see cref="AgentPushError.StaleVersion"/>; <c>null</c> otherwise.</summary>
    public int? CurrentVersion { get; init; }

    /// <summary>The removed agent's last version under this name, for <see cref="AgentPushError.Removed"/>.</summary>
    public int? LastVersion { get; init; }

    /// <summary>The removed agent's name now, for a <see cref="AgentPushError.Removed"/> rename.</summary>
    public string? RenamedTo { get; init; }

    public AgentPushError Error { get; init; } = AgentPushError.None;
    public string? Message { get; init; }

    public static AgentPushResult Stored(AgentPushStatus status, string name, AgentVersion version, Guid agentId, ProjectResolution? project) =>
        new()
        {
            Status = status,
            Name = name,
            Version = version.VersionNumber,
            AgentId = agentId,
            VersionId = version.Id,
            Path = version.Path.Value,
            Project = project,
        };

    public static AgentPushResult Stale(string name, int currentVersion, string message, ProjectResolution? project) =>
        new()
        {
            Status = AgentPushStatus.Failed,
            Name = name,
            Error = AgentPushError.StaleVersion,
            CurrentVersion = currentVersion,
            Message = message,
            Project = project,
        };

    public static AgentPushResult Removed(AgentTombstone tombstone, int baseVersion, ProjectResolution? project) =>
        new()
        {
            Status = AgentPushStatus.Failed,
            Name = tombstone.Name,
            Error = AgentPushError.Removed,
            LastVersion = tombstone.LastVersion,
            RenamedTo = tombstone.RenamedTo,
            Message = tombstone.RenamedTo is { } renamedTo
                ? $"This copy was based on version {baseVersion} of '{tombstone.Name}', which was renamed to '{renamedTo}' in the registry at version {tombstone.LastVersion}. Nothing was stored."
                : $"This copy was based on version {baseVersion} of '{tombstone.Name}', which was deleted from the registry at version {tombstone.LastVersion}. Nothing was stored.",
            Project = project,
        };

    public static AgentPushResult Failed(string? name, AgentPushError error, string message) =>
        new() { Status = AgentPushStatus.Failed, Name = name, Error = error, Message = message };
}
