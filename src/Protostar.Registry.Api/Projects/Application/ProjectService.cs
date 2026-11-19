using Microsoft.EntityFrameworkCore;
using Npgsql;
using Protostar.Registry.Api.Common;
using Protostar.Registry.Api.Infrastructure;

namespace Protostar.Registry.Api.Projects;

/// <summary>Application service that resolves a directory's identity to one of the owner's projects.</summary>
/// <remarks>
/// Loads the keys that could match, lets <see cref="ProjectMatcher"/> decide, and attaches the keys the
/// matched or new project should carry. Showing, linking and unlinking go through the same match, so "which
/// project is this folder in" can never disagree with where its sessions are filed. Every operation is scoped
/// to <c>ownerId</c>: projects are per owner.
/// </remarks>
public sealed class ProjectService
{
    private readonly RegistryDbContext _db;
    private readonly IFolderFilings _filings;
    private readonly IDomainEventDispatcher _events;

    public ProjectService(RegistryDbContext db, IFolderFilings filings, IDomainEventDispatcher events)
    {
        _db = db;
        _filings = filings;
        _events = events;
    }

    /// <summary>Finds or creates the project for <paramref name="identity"/>, and attaches the keys it should carry.</summary>
    /// <param name="ownerId">The caller.</param>
    /// <param name="identity">The directory's identity.</param>
    /// <param name="cancellationToken">Cancels the lookups and the save.</param>
    /// <remarks>
    /// The project and its keys are saved before this returns, without dispatching the events they raised; the
    /// caller's <c>SaveChangesAndDispatchAsync</c> dispatches them with its own changes. Two clients resolving
    /// the same new repo at once collide on the unique key index; the loser clears the change tracker, looks
    /// again and finds the winner's project. So call this before tracking changes of your own.
    /// </remarks>
    public async Task<ProjectResolution> ResolveAsync(
        Guid ownerId, ProjectIdentity identity, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            var match = await MatchAsync(ownerId, identity, cancellationToken);

            var now = DateTimeOffset.UtcNow;
            var project = match.ProjectId is { } id
                ? await _db.Projects.Include(p => p.Keys).SingleAsync(p => p.Id == id, cancellationToken)
                : _db.Projects.Add(Project.Create(ownerId, identity.Name, now)).Entity;

            if (ProjectMatcher.Renames(identity, match.RecognisedBy))
                project.Rename(identity.Name, now);

            var toAttach = ProjectMatcher.KeysToAttach(identity, match.RecognisedBy);
            var claimed = await KnownKeysAsync(ownerId, toAttach, cancellationToken);
            foreach (var (kind, key) in toAttach)
            {
                if (!claimed.Any(k => k.Kind == kind && k.Key == key && k.ProjectId != project.Id))
                    project.Attach(kind, key, now);
            }

            try
            {
                await _db.SaveChangesAsync(cancellationToken);
                return new ProjectResolution(project.Id, project.Name, match.RecognisedBy);
            }
            catch (DbUpdateException ex) when (attempt < 3 && ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                // Another request attached one of these keys first. Forget this attempt and resolve again.
                _db.ChangeTracker.Clear();
            }
        }
    }

    /// <summary>Deletes the projects among <paramref name="projectIds"/> that only ever stood for a folder (<see cref="Project.StandsOnlyForFolders"/>).</summary>
    /// <remarks>
    /// The caller checks no transcript is filed under them. A project that has skills is kept: its skills are
    /// keyed by it, and deleting it would either fail on the foreign key or orphan their history.
    /// </remarks>
    public async Task ForgetFolderProjectsAsync(Guid ownerId, IReadOnlyCollection<Guid> projectIds, CancellationToken cancellationToken)
    {
        if (projectIds.Count == 0)
            return;
        var projects = await _db.Projects.Include(p => p.Keys)
            .Where(p => p.OwnerId == ownerId && projectIds.Contains(p.Id))
            .Where(p => !_db.Skills.Any(s => s.ProjectId == p.Id))
            .ToListAsync(cancellationToken);
        _db.Projects.RemoveRange(projects.Where(p => p.StandsOnlyForFolders));
        await _db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>The project <paramref name="identity"/> belongs to, without creating or attaching anything.</summary>
    /// <remarks>For a folder outside git that is not linked, also the projects it looks like (never applied).</remarks>
    public async Task<ProjectView> ShowAsync(Guid ownerId, ProjectIdentity identity, CancellationToken cancellationToken)
    {
        var match = await MatchAsync(ownerId, identity, cancellationToken);
        var name = match.ProjectId is { } id
            ? await _db.Projects.Where(p => p.Id == id).Select(p => p.Name).SingleAsync(cancellationToken)
            : identity.Name;

        IReadOnlyList<ProjectSuggestion> suggestions = [];
        if (identity.RootCommit is null && !identity.Shallow && match.RecognisedBy != ProjectRecognition.Link)
        {
            var projects = await _db.Projects.AsNoTracking().Include(p => p.Keys).Where(p => p.OwnerId == ownerId).ToListAsync(cancellationToken);
            suggestions = ProjectSuggestions.For(identity, match.RecognisedBy, match.ProjectId, projects);
        }

        return new ProjectView(match.ProjectId, name, match.RecognisedBy, identity.Folder, identity.Subpath, suggestions);
    }

    /// <summary>Links the folder of <paramref name="identity"/> to the project named by <paramref name="target"/>, an id or an exact name.</summary>
    /// <remarks>Replaces any link the folder had. The folder's transcripts move with it (<see cref="FolderLinkChanged"/>).</remarks>
    public async Task<ProjectLinkResult> LinkAsync(Guid ownerId, ProjectIdentity identity, string target, CancellationToken cancellationToken)
    {
        var candidates = Guid.TryParse(target, out var targetId)
            ? await _db.Projects.Include(p => p.Keys).Where(p => p.OwnerId == ownerId && p.Id == targetId).ToListAsync(cancellationToken)
            : await _db.Projects.Include(p => p.Keys).Where(p => p.OwnerId == ownerId && p.Name.ToLower() == target.ToLower()).ToListAsync(cancellationToken);
        if (candidates.Count == 0)
            return ProjectLinkResult.Fail($"No project '{target}'. Use its id, or its full name as `protostar project` or `sessions list` shows it.");
        if (candidates.Count > 1)
            return ProjectLinkResult.Fail($"More than one project is named '{target}'. Use its id: {string.Join(", ", candidates.Select(p => p.Id))}.");

        var project = candidates[0];
        var now = DateTimeOffset.UtcNow;
        var linked = await LinkedProjectAsync(ownerId, identity, cancellationToken);
        if (linked is not null && linked.Id != project.Id)
            linked.Unlink(identity.FolderKey, now);
        project.Link(identity.FolderKey, now);

        await _db.SaveChangesAndDispatchAsync(_events, cancellationToken);
        return ProjectLinkResult.Ok(ProjectLinkOutcome.Linked, await ShowAsync(ownerId, identity, cancellationToken));
    }

    /// <summary>Unlinks the folder of <paramref name="identity"/>: back to where it would belong on its own, or, if it already is there by a shared key, apart.</summary>
    /// <remarks>
    /// A linked folder loses its link. A folder that belongs to a project by a key other copies share (a fork by
    /// its upstream's root commit, ADR 0002's owner decision 1) is given a project of its own and linked to it,
    /// so its sessions stay apart. A folder that is already its own project is left alone.
    /// </remarks>
    public async Task<ProjectLinkResult> UnlinkAsync(Guid ownerId, ProjectIdentity identity, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        ProjectLinkOutcome outcome;
        if (await LinkedProjectAsync(ownerId, identity, cancellationToken) is { } linked)
        {
            linked.Unlink(identity.FolderKey, now);
            outcome = ProjectLinkOutcome.Unlinked;
        }
        else if ((await MatchAsync(ownerId, identity, cancellationToken)).RecognisedBy is ProjectRecognition.RootCommit or ProjectRecognition.PreviousRootCommit or ProjectRecognition.Remote)
        {
            var own = Project.Create(ownerId, $"{identity.Name} ({identity.Folder})", now);
            _db.Projects.Add(own);
            own.Link(identity.FolderKey, now);
            outcome = ProjectLinkOutcome.Separated;
        }
        else
        {
            return ProjectLinkResult.Ok(ProjectLinkOutcome.NothingToUnlink, await ShowAsync(ownerId, identity, cancellationToken));
        }

        await _db.SaveChangesAndDispatchAsync(_events, cancellationToken);
        return ProjectLinkResult.Ok(outcome, await ShowAsync(ownerId, identity, cancellationToken));
    }

    private async Task<ProjectMatch> MatchAsync(Guid ownerId, ProjectIdentity identity, CancellationToken cancellationToken)
    {
        var known = await KnownKeysAsync(ownerId, ProjectMatcher.CandidateKeys(identity), cancellationToken);
        var folderProjects = await _filings.ProjectsOfFolderAsync(ownerId, identity, cancellationToken);
        return ProjectMatcher.Match(identity, known, folderProjects);
    }

    private async Task<Project?> LinkedProjectAsync(Guid ownerId, ProjectIdentity identity, CancellationToken cancellationToken)
    {
        var projectId = await _db.ProjectKeys
            .Where(k => k.OwnerId == ownerId && k.Kind == ProjectKeyKind.Link && k.Key == identity.FolderKey)
            .Select(k => (Guid?)k.ProjectId)
            .FirstOrDefaultAsync(cancellationToken);
        return projectId is { } id ? await _db.Projects.Include(p => p.Keys).SingleAsync(p => p.Id == id, cancellationToken) : null;
    }

    private async Task<List<ProjectKey>> KnownKeysAsync(
        Guid ownerId, IReadOnlyList<(ProjectKeyKind Kind, string Key)> keys, CancellationToken cancellationToken)
    {
        if (keys.Count == 0)
            return [];
        var values = keys.Select(k => k.Key).Distinct().ToList();
        var found = await _db.ProjectKeys.AsNoTracking()
            .Where(k => k.OwnerId == ownerId && values.Contains(k.Key))
            .ToListAsync(cancellationToken);
        return found.Where(f => keys.Contains((f.Kind, f.Key))).ToList();
    }
}

/// <summary>The project a directory resolved to.</summary>
/// <param name="ProjectId">The project.</param>
/// <param name="Name">Its display name.</param>
/// <param name="RecognisedBy">How it was matched.</param>
public sealed record ProjectResolution(Guid ProjectId, string Name, ProjectRecognition RecognisedBy);

/// <summary>Which project a folder belongs to, as <c>protostar project</c> shows it.</summary>
/// <param name="ProjectId">The project, or <c>null</c> when none exists yet: the first backup from here would create it.</param>
/// <param name="Name">Its display name, or the name it would get.</param>
/// <param name="RecognisedBy">How the folder was matched.</param>
/// <param name="Folder">The folder it is filed by on its machine: the main worktree for git.</param>
/// <param name="Subpath">The path within the repo, when in a subfolder.</param>
/// <param name="Suggestions">Projects a folder outside git looks like; never applied without the user.</param>
public sealed record ProjectView(
    Guid? ProjectId, string Name, ProjectRecognition RecognisedBy, string Folder, string? Subpath, IReadOnlyList<ProjectSuggestion> Suggestions);

/// <summary>What linking or unlinking did.</summary>
public enum ProjectLinkOutcome
{
    /// <summary>The folder is now linked to the project asked for.</summary>
    Linked,

    /// <summary>The folder's link was removed; it belongs where it would on its own.</summary>
    Unlinked,

    /// <summary>The folder shared its project by a common key (a fork) and now has a project of its own.</summary>
    Separated,

    /// <summary>The folder was not linked and already is its own project.</summary>
    NothingToUnlink,
}

/// <summary>The result of a link or unlink: the outcome and where the folder belongs now, or why it failed.</summary>
public sealed record ProjectLinkResult(ProjectLinkOutcome Outcome, ProjectView? View, string? Error)
{
    public static ProjectLinkResult Ok(ProjectLinkOutcome outcome, ProjectView view) => new(outcome, view, null);

    public static ProjectLinkResult Fail(string error) => new(default, null, error);
}

/// <summary>Which projects a folder's sessions are filed under now: the port the Sessions feature fills.</summary>
/// <remarks>
/// Keeps Projects free of any dependency on Sessions. Used for ADR 0002's "first-parent root moved" rule: a
/// checkout stays on a project it is already filed under.
/// </remarks>
public interface IFolderFilings
{
    /// <summary>The projects transcripts from the identity's machine and folder are filed under.</summary>
    Task<IReadOnlyCollection<Guid>> ProjectsOfFolderAsync(Guid ownerId, ProjectIdentity identity, CancellationToken cancellationToken);
}
