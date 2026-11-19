using Microsoft.EntityFrameworkCore;
using Npgsql;
using Protostar.Registry.Api.Infrastructure;

namespace Protostar.Registry.Api.Projects;

/// <summary>Application service that resolves a directory's identity to one of the owner's projects.</summary>
/// <remarks>
/// Loads the keys that could match, lets <see cref="ProjectMatcher"/> decide, and attaches the keys the
/// matched or new project should carry. Every operation is scoped to <c>ownerId</c>: projects are per owner.
/// </remarks>
public sealed class ProjectService
{
    private readonly RegistryDbContext _db;

    public ProjectService(RegistryDbContext db) => _db = db;

    /// <summary>Finds or creates the project for <paramref name="identity"/>, and attaches the keys it should carry.</summary>
    /// <param name="ownerId">The caller.</param>
    /// <param name="identity">The directory's identity.</param>
    /// <param name="folderProjects">Projects that transcripts from the same machine and folder are filed under now.</param>
    /// <param name="cancellationToken">Cancels the lookups and the save.</param>
    /// <remarks>
    /// The project and its keys are saved before this returns, without dispatching the events they raised; the
    /// caller's <c>SaveChangesAndDispatchAsync</c> dispatches them with its own changes. Two clients resolving
    /// the same new repo at once collide on the unique key index; the loser clears the change tracker, looks
    /// again and finds the winner's project. So call this before tracking changes of your own.
    /// </remarks>
    public async Task<ProjectResolution> ResolveAsync(
        Guid ownerId, ProjectIdentity identity, IReadOnlyCollection<Guid> folderProjects, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            var candidates = ProjectMatcher.CandidateKeys(identity);
            var known = await KnownKeysAsync(ownerId, candidates, cancellationToken);
            var match = ProjectMatcher.Match(identity, known, folderProjects);

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
