using Protostar.Registry.Api.Common;

namespace Protostar.Registry.Api.Projects;

/// <summary>What a key attached to a project identifies.</summary>
public enum ProjectKeyKind
{
    /// <summary>A git repo, by the first-parent root commit of its default branch. Same on every machine and path.</summary>
    RootCommit,

    /// <summary>A normalised remote URL, attached by full clones so a shallow clone of the same repo can be matched.</summary>
    Remote,

    /// <summary>A folder on one machine (<c>&lt;machine id&gt;:&lt;path&gt;</c>), for directories with no better key.</summary>
    Folder,
}

/// <summary>A project: the one identity sessions, memory and search share for "the same project", on every machine.</summary>
/// <remarks>
/// <para>
/// A project has an opaque id and a display name, and owns the keys that resolve to it
/// (<see cref="ProjectKey"/>). Each key belongs to at most one of an owner's projects; the database enforces
/// that across aggregates. Transcripts reference a project by id. See ADR 0002.
/// </para>
/// </remarks>
public sealed class Project : AggregateRoot
{
    /// <summary>The longest display name kept.</summary>
    public const int MaxNameLength = 500;

    private readonly List<ProjectKey> _keys = [];

    private Project()
    {
    }

    public Guid Id { get; private set; }

    public Guid OwnerId { get; private set; }

    /// <summary>The display name: a normalised remote URL, or a folder name. Display only; never matched on.</summary>
    public string Name { get; private set; } = default!;

    public IReadOnlyList<ProjectKey> Keys => _keys;

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Creates a project named <paramref name="name"/>.</summary>
    public static Project Create(Guid ownerId, string name, DateTimeOffset now)
    {
        var project = new Project { Id = Guid.NewGuid(), OwnerId = ownerId, CreatedAt = now };
        project.Rename(name, now);
        return project;
    }

    /// <summary>Attaches <paramref name="key"/> to this project, unless it already is. Returns whether it was added.</summary>
    /// <remarks>
    /// The caller checks the key is not attached to another of the owner's projects. Attaching a remote raises
    /// <see cref="ProjectRemoteAttached"/>, because shallow clones that were filed by folder can now be matched.
    /// </remarks>
    public bool Attach(ProjectKeyKind kind, string key, DateTimeOffset now)
    {
        if (_keys.Any(k => k.Kind == kind && k.Key == key))
            return false;

        _keys.Add(ProjectKey.Create(Id, OwnerId, kind, key, now));
        UpdatedAt = now;
        if (kind == ProjectKeyKind.Remote)
            RaiseDomainEvent(new ProjectRemoteAttached(OwnerId, Id, key));
        return true;
    }

    /// <summary>Changes the display name. Latest seen wins (ADR 0002).</summary>
    public void Rename(string name, DateTimeOffset now)
    {
        var trimmed = string.IsNullOrWhiteSpace(name) ? "unnamed" : name.Trim();
        trimmed = trimmed.Length <= MaxNameLength ? trimmed : trimmed[..MaxNameLength];
        if (trimmed == Name)
            return;
        Name = trimmed;
        UpdatedAt = now;
    }
}

/// <summary>One key that resolves to a project.</summary>
public sealed class ProjectKey
{
    /// <summary>The longest key: a machine id, a colon and a 2000-character path, with room to spare.</summary>
    public const int MaxLength = 2100;

    private ProjectKey()
    {
    }

    public Guid Id { get; private set; }

    public Guid ProjectId { get; private set; }

    /// <summary>The project's owner, carried so a key can be unique per owner.</summary>
    public Guid OwnerId { get; private set; }

    public ProjectKeyKind Kind { get; private set; }

    /// <summary>A commit hash, a normalised remote, or <c>&lt;machine id&gt;:&lt;path&gt;</c>, by <see cref="Kind"/>.</summary>
    public string Key { get; private set; } = default!;

    public DateTimeOffset CreatedAt { get; private set; }

    internal static ProjectKey Create(Guid projectId, Guid ownerId, ProjectKeyKind kind, string key, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(key) || key.Length > MaxLength)
            throw new ArgumentException($"A project key must be 1 to {MaxLength} characters.", nameof(key));
        return new ProjectKey { Id = Guid.NewGuid(), ProjectId = projectId, OwnerId = ownerId, Kind = kind, Key = key, CreatedAt = now };
    }
}
