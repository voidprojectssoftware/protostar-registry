namespace Protostar.Registry.Api.Projects;

/// <summary>The keys and display name a working directory on one machine is resolved by, derived from its <see cref="ProjectFacts"/>.</summary>
/// <remarks>
/// <para>
/// This is where ADR 0002's rule lives on the registry side. A git checkout is identified by its root commit
/// (<see cref="RootCommit"/>), unless it is a shallow clone, which can only be matched by a remote some full
/// clone already attached (<see cref="Remotes"/>). Anything else is identified by its machine and folder
/// (<see cref="FolderKey"/>). For a git checkout the folder is the main worktree, so every worktree of a repo
/// on a machine has the same folder; for any other directory it is the nearest existing folder.
/// </para>
/// <para>
/// The display name is the normalised remote (<c>origin</c> first), or else the folder's last segment.
/// </para>
/// </remarks>
public sealed record ProjectIdentity
{
    private ProjectIdentity()
    {
    }

    /// <summary>The first-parent root commit of the default branch; <c>null</c> for a shallow clone or a non-git folder.</summary>
    public string? RootCommit { get; private init; }

    /// <summary>Every root reachable from <c>HEAD</c>, for keeping a checkout on its project when its first-parent root moves.</summary>
    public IReadOnlyList<string> Roots { get; private init; } = [];

    /// <summary>Normalised remotes, <c>origin</c> first, without duplicates.</summary>
    public IReadOnlyList<string> Remotes { get; private init; } = [];

    /// <summary>Whether this is a git checkout whose root commit cannot be trusted, so only <see cref="Remotes"/> can match it.</summary>
    public bool Shallow { get; private init; }

    /// <summary>The folder the directory belongs to on its machine: the main worktree for git, else the directory itself.</summary>
    public string Folder { get; private init; } = "";

    /// <summary><c>&lt;machine id&gt;:&lt;folder&gt;</c>, the key for a project that has no better one.</summary>
    public string FolderKey { get; private init; } = "";

    /// <summary>The path within the repo, or <c>null</c> at its top or outside git.</summary>
    public string? Subpath { get; private init; }

    /// <summary>What a project found or created from this identity is called.</summary>
    public string Name { get; private init; } = "";

    /// <summary>The identity of a directory a client described with git facts.</summary>
    public static ProjectIdentity From(Guid machineId, ProjectFacts facts)
    {
        var folder = facts.IsGit ? facts.MainWorktree ?? facts.TopLevel ?? facts.Directory : facts.Directory;
        var remotes = facts.IsGit
            ? facts.Remotes
                .OrderBy(r => r.Name == "origin" ? 0 : 1)
                .Select(r => RemoteUrl.Normalise(r.Url))
                .Where(r => r.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToList()
            : [];

        return new ProjectIdentity
        {
            RootCommit = facts.TrustedRootCommit,
            Roots = facts.Shallow ? [] : facts.Roots,
            Remotes = remotes,
            Shallow = facts.IsGit && facts.TrustedRootCommit is null,
            Folder = folder,
            FolderKey = FolderKeyFor(machineId, folder),
            Subpath = string.IsNullOrEmpty(facts.Subpath) ? null : facts.Subpath.TrimEnd('/', '\\'),
            Name = remotes.FirstOrDefault() ?? LastSegment(folder),
        };
    }

    /// <summary>The identity of a working directory known only by its path, as for transcripts backed up before clients sent facts.</summary>
    public static ProjectIdentity FromFolder(Guid machineId, string folder) => new()
    {
        Folder = folder,
        FolderKey = FolderKeyFor(machineId, folder),
        Name = LastSegment(folder),
    };

    /// <summary>The folder key for <paramref name="folder"/> on <paramref name="machineId"/>.</summary>
    /// <remarks>Paths are compared exactly as recorded, case included.</remarks>
    public static string FolderKeyFor(Guid machineId, string folder) => $"{machineId}:{folder}";

    private static string LastSegment(string path) =>
        path.TrimEnd('/', '\\').Split('/', '\\').LastOrDefault(s => s.Length > 0) ?? path;
}
