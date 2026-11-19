namespace Protostar.Registry.Api.Projects;

/// <summary>How a directory was matched to its project.</summary>
public enum ProjectRecognition
{
    /// <summary>By the repo's root commit.</summary>
    RootCommit,

    /// <summary>By a root commit this checkout used to have: its first-parent root moved, and it stayed on its project.</summary>
    PreviousRootCommit,

    /// <summary>A shallow clone, by a remote a full clone of the same repo attached.</summary>
    Remote,

    /// <summary>By its machine and folder.</summary>
    Folder,

    /// <summary>Nothing matched; a new project is (or would be) created.</summary>
    New,
}

/// <summary>The project a directory resolves to, and how.</summary>
/// <param name="ProjectId">The matched project, or <c>null</c> for <see cref="ProjectRecognition.New"/>.</param>
/// <param name="RecognisedBy">Which rule matched.</param>
public sealed record ProjectMatch(Guid? ProjectId, ProjectRecognition RecognisedBy);

/// <summary>Decides which project a <see cref="ProjectIdentity"/> belongs to, from the keys already attached.</summary>
/// <remarks>
/// <para>
/// Order, first match wins: the root commit; a recorded root of this checkout that is attached to a project this
/// same machine-and-folder is already filed under (the first-parent root moved, ADR 0002's trap); for a shallow
/// clone, a remote (<c>origin</c> first); the machine and folder; otherwise a new project.
/// </para>
/// <para>
/// A checkout with a root commit never falls back to its folder: the root commit is its identity, so a folder
/// that becomes a git repo starts a new project rather than inheriting the folder's.
/// </para>
/// </remarks>
public static class ProjectMatcher
{
    /// <summary>Every key that could match <paramref name="identity"/>; look these up, then call <see cref="Match"/>.</summary>
    public static IReadOnlyList<(ProjectKeyKind Kind, string Key)> CandidateKeys(ProjectIdentity identity)
    {
        var keys = new List<(ProjectKeyKind, string)>();
        if (identity.RootCommit is { } root)
        {
            keys.Add((ProjectKeyKind.RootCommit, root));
            keys.AddRange(identity.Roots.Where(r => r != root).Select(r => (ProjectKeyKind.RootCommit, r)));
            return keys;
        }

        if (identity.Shallow)
            keys.AddRange(identity.Remotes.Select(r => (ProjectKeyKind.Remote, r)));
        keys.Add((ProjectKeyKind.Folder, identity.FolderKey));
        return keys;
    }

    /// <summary>The project <paramref name="identity"/> resolves to.</summary>
    /// <param name="identity">The directory's identity.</param>
    /// <param name="known">Attached keys found for <see cref="CandidateKeys"/>; others are ignored.</param>
    /// <param name="folderProjects">Projects that transcripts from this machine and folder are filed under now.</param>
    public static ProjectMatch Match(ProjectIdentity identity, IReadOnlyCollection<ProjectKey> known, IReadOnlyCollection<Guid> folderProjects)
    {
        Guid? Find(ProjectKeyKind kind, string key) => known.FirstOrDefault(k => k.Kind == kind && k.Key == key)?.ProjectId;

        if (identity.RootCommit is { } root)
        {
            if (Find(ProjectKeyKind.RootCommit, root) is { } byRoot)
                return new ProjectMatch(byRoot, ProjectRecognition.RootCommit);

            foreach (var previous in identity.Roots.Where(r => r != root))
            {
                if (Find(ProjectKeyKind.RootCommit, previous) is { } byPrevious && folderProjects.Contains(byPrevious))
                    return new ProjectMatch(byPrevious, ProjectRecognition.PreviousRootCommit);
            }

            return new ProjectMatch(null, ProjectRecognition.New);
        }

        if (identity.Shallow)
        {
            foreach (var remote in identity.Remotes)
            {
                if (Find(ProjectKeyKind.Remote, remote) is { } byRemote)
                    return new ProjectMatch(byRemote, ProjectRecognition.Remote);
            }
        }

        return Find(ProjectKeyKind.Folder, identity.FolderKey) is { } byFolder
            ? new ProjectMatch(byFolder, ProjectRecognition.Folder)
            : new ProjectMatch(null, ProjectRecognition.New);
    }

    /// <summary>The keys the matched (or new) project should carry for <paramref name="identity"/>.</summary>
    /// <remarks>
    /// A full clone attaches its root commit and its remotes, the remotes so later shallow clones can be matched.
    /// A shallow clone matched by remote attaches nothing. Anything else attaches its folder key. A key already
    /// attached to another project stays there: first claim wins.
    /// </remarks>
    public static IReadOnlyList<(ProjectKeyKind Kind, string Key)> KeysToAttach(ProjectIdentity identity, ProjectRecognition recognisedBy)
    {
        if (identity.RootCommit is { } root)
            return [(ProjectKeyKind.RootCommit, root), .. identity.Remotes.Select(r => (ProjectKeyKind.Remote, r))];
        return recognisedBy == ProjectRecognition.Remote ? [] : [(ProjectKeyKind.Folder, identity.FolderKey)];
    }

    /// <summary>Whether the matched project should take <paramref name="identity"/>'s name: only a repo's remote renames it.</summary>
    public static bool Renames(ProjectIdentity identity, ProjectRecognition recognisedBy) =>
        identity.Remotes.Count > 0 && recognisedBy is ProjectRecognition.RootCommit or ProjectRecognition.PreviousRootCommit or ProjectRecognition.Remote;
}
