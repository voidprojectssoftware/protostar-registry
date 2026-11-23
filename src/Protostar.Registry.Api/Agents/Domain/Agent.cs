using Protostar.Registry.Api.Common;
using Protostar.Registry.Api.Identity;
using Protostar.Registry.Api.Projects;
using Protostar.Registry.Api.Skills;

namespace Protostar.Registry.Api.Agents;

/// <summary>The custom agent aggregate root: a creator, an optional project and a name, owning its version history.</summary>
/// <remarks>
/// <para>
/// Built in the <see cref="Skills.Skill"/> aggregate's image and deliberately copied rather than shared (ADR 0003): the
/// same invariants (creator-only writes, contiguous version numbers, current = newest, idempotent re-push, stale
/// refusal, numbering after a tombstone) hold for both, and a fix to one copy must be checked against the other.
/// </para>
/// <para>
/// An agent is identified by (creator, project, name), where no project means a personal agent. Its name is the
/// <c>name</c> field of its front matter, not its file name, and each version holds the one file it consists of.
/// </para>
/// </remarks>
public sealed class Agent : AggregateRoot, IMovesWithFolder
{
    private readonly List<AgentVersion> _versions = [];

    // The version number the first push takes: above 1 only for a new agent that reuses a removed agent's name.
    private int _firstVersionNumber = 1;

    private Agent()
    {
    }

    public Guid Id { get; private set; }

    /// <summary>The user who first pushed this agent; it is namespaced to them. Provenance, not a permissions grant.</summary>
    public Guid CreatorId { get; private set; }

    public User Creator { get; private set; } = default!;

    /// <summary>The project this agent belongs to, or <c>null</c> for a personal agent (one in the harness's own config directory).</summary>
    /// <remarks>It changes only when the folder it belongs to is linked or unlinked (<see cref="FolderMove"/>).</remarks>
    public Guid? ProjectId { get; private set; }

    /// <summary>
    /// The folder (<c>&lt;machine id&gt;:&lt;path&gt;</c>) whose link or unlink carried this agent into a project other
    /// folders share; <c>null</c> when it is where it was pushed, or in a project that is its folder's alone.
    /// </summary>
    public string? CarriedByFolderKey { get; private set; }

    /// <summary>The agent's name, from its front matter. Unique per creator and project.</summary>
    public string Name { get; private set; } = default!;

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>The most recently pushed version; null only before the first version is pushed.</summary>
    public Guid? CurrentVersionId { get; private set; }

    public AgentVersion? CurrentVersion { get; private set; }

    /// <summary>Every version ever pushed.</summary>
    public IReadOnlyCollection<AgentVersion> Versions => _versions;

    /// <summary>True when <paramref name="name"/> can name an agent: not blank and free of <c>/</c> and <c>\</c>.</summary>
    /// <remarks>An agent is addressed by name in URLs (<c>/v1/agents/{name}/rename</c>), so a separator would make it unreachable.</remarks>
    public static bool IsValidName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && name.IndexOfAny(['/', '\\']) < 0;

    /// <summary>Starts a new agent owned by <paramref name="creatorId"/>. Its first version is added by a push.</summary>
    /// <param name="creatorId">The user pushing it.</param>
    /// <param name="name">The agent's name.</param>
    /// <param name="now">When it is created.</param>
    /// <param name="projectId">The project it belongs to; <c>null</c> for a personal agent.</param>
    /// <param name="replacing">The tombstone of a removed agent whose name (in the same scope) this one takes; versions number after it.</param>
    /// <exception cref="ArgumentException"><paramref name="name"/> fails <see cref="IsValidName"/>, or <paramref name="replacing"/> is another name's or scope's.</exception>
    public static Agent Create(Guid creatorId, string name, DateTimeOffset now, Guid? projectId = null, AgentTombstone? replacing = null)
    {
        if (replacing is not null && (replacing.CreatorId != creatorId || replacing.ProjectId != projectId || replacing.Name != name))
            throw new ArgumentException("An agent only replaces the tombstone of its own name and scope.", nameof(replacing));
        if (!IsValidName(name))
            throw new ArgumentException("An agent name must not be blank or contain '/' or '\\'.", nameof(name));

        return new Agent
        {
            Id = Guid.NewGuid(),
            CreatorId = creatorId,
            ProjectId = projectId,
            Name = name,
            CreatedAt = now,
            _firstVersionNumber = (replacing?.LastVersion ?? 0) + 1,
        };
    }

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">This is a personal agent, which belongs to no folder.</exception>
    public void MoveWithFolder(Guid projectId, string? carriedByFolderKey)
    {
        if (ProjectId is null)
            throw new InvalidOperationException("A personal agent belongs to no folder, so it never moves with one.");
        ProjectId = projectId;
        CarriedByFolderKey = carriedByFolderKey;
    }

    /// <summary>Renames this agent, keeping its identity and whole version history.</summary>
    /// <param name="renamedBy">The user renaming it.</param>
    /// <param name="newName">The name it takes.</param>
    /// <param name="namesInScope">The names of every agent of the same creator and project, which the new name must not take.</param>
    /// <returns><c>false</c> when <paramref name="newName"/> is already this agent's name, so nothing changed.</returns>
    /// <remarks>The stored files are not rewritten: their front matter keeps the name they were pushed with, as a skill's SKILL.md does.</remarks>
    /// <exception cref="AgentCreatorViolationException"><paramref name="renamedBy"/> is not this agent's creator.</exception>
    /// <exception cref="ArgumentException"><paramref name="newName"/> fails <see cref="IsValidName"/>.</exception>
    /// <exception cref="AgentNameTakenException">Another agent in the same scope already has <paramref name="newName"/>.</exception>
    public bool Rename(Guid renamedBy, string newName, IEnumerable<string> namesInScope)
    {
        EnsureCreator(renamedBy);
        if (!IsValidName(newName))
            throw new ArgumentException("An agent name must not be blank or contain '/' or '\\'.", nameof(newName));
        if (newName == Name)
            return false;
        if (namesInScope.Contains(newName, StringComparer.Ordinal))
            throw new AgentNameTakenException(newName);

        Name = newName;
        return true;
    }

    /// <summary>Checks that <paramref name="deletedBy"/> may delete this agent with every version.</summary>
    /// <remarks>The caller removes the row; the versions cascade from it.</remarks>
    /// <exception cref="AgentCreatorViolationException"><paramref name="deletedBy"/> is not this agent's creator.</exception>
    public void EnsureCanDelete(Guid deletedBy) => EnsureCreator(deletedBy);

    /// <summary>
    /// Pushes a new version of this agent: an idempotent no-op when the file's bytes match the current version, otherwise
    /// a new version that becomes current and raises <see cref="AgentVersionPushed"/>.
    /// </summary>
    /// <param name="pushedBy">The user pushing.</param>
    /// <param name="manifest">The parsed front matter.</param>
    /// <param name="path">The file's path relative to the agents folder (agents may sit in subfolders).</param>
    /// <param name="content">The file's bytes.</param>
    /// <param name="now">When it is pushed.</param>
    /// <param name="baseVersion">The version the pusher's copy was last synced from (0 for none), or <c>null</c> when it does not say.</param>
    /// <remarks>
    /// The rules are <see cref="Skills.Skill.PushVersion"/>'s. Unchanged means the same bytes: a file moved to another
    /// subfolder with nothing else changed is the same version, so the path recorded is the one it was first pushed with.
    /// </remarks>
    /// <exception cref="AgentCreatorViolationException"><paramref name="pushedBy"/> is not this agent's creator.</exception>
    /// <exception cref="AgentSizeExceededException">The file exceeds <see cref="AgentSizePolicy"/>.</exception>
    /// <exception cref="AgentVersionStaleException"><paramref name="baseVersion"/> is given and is not the current version.</exception>
    public AgentVersionPushOutcome PushVersion(
        Guid pushedBy, AgentManifest manifest, RelativePath path, byte[] content, DateTimeOffset now, int? baseVersion = null)
    {
        EnsureCreator(pushedBy);

        if (AgentSizePolicy.Exceeded(content.LongLength) is { } reason)
            throw new AgentSizeExceededException(reason);

        var contentHash = Sha256Hash.Of(content);
        if (CurrentVersion is { } current && current.ContentHash == contentHash)
            return AgentVersionPushOutcome.Unchanged(current);

        if (baseVersion is { } based && CurrentVersion is { } latest && based != latest.VersionNumber)
            throw new AgentVersionStaleException(based, latest.VersionNumber);

        var versionNumber = CurrentVersion is { } previous ? previous.VersionNumber + 1 : _firstVersionNumber;
        var version = AgentVersion.Create(Id, versionNumber, manifest, path, content, contentHash, pushedBy, now);

        _versions.Add(version);
        CurrentVersion = version;
        CurrentVersionId = version.Id;

        RaiseDomainEvent(new AgentVersionPushed(Id, version.Id, versionNumber, pushedBy, now));
        return AgentVersionPushOutcome.Created(version);
    }

    // Agents are namespaced per creator, so a change never legitimately crosses creators.
    private void EnsureCreator(Guid userId)
    {
        if (CreatorId != userId)
            throw new AgentCreatorViolationException(Id, userId);
    }
}
