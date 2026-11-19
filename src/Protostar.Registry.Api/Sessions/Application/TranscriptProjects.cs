using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Protostar.Registry.Api.Common;
using Protostar.Registry.Api.Infrastructure;
using Protostar.Registry.Api.Projects;

namespace Protostar.Registry.Api.Sessions;

/// <summary>Files transcripts under the project of their working directory, and refiles them when the mapping changes.</summary>
/// <remarks>
/// <para>
/// A transcript registered with project facts is resolved from those (ADR 0002). One registered without them
/// (an older client, or a transcript backed up before facts existed) is resolved from the working directory its
/// lines recorded, as a machine-and-folder project, until a later registration brings facts.
/// </para>
/// <para>
/// Refiling re-resolves from what each transcript has stored, without the client. Transcripts are grouped by
/// machine, folder and facts first, so a history of thousands of transcripts in a handful of folders costs a
/// handful of resolutions.
/// </para>
/// </remarks>
public sealed class TranscriptProjects
{
    private readonly RegistryDbContext _db;
    private readonly ProjectService _projects;

    public TranscriptProjects(RegistryDbContext db, ProjectService projects)
    {
        _db = db;
        _projects = projects;
    }

    /// <summary>Resolves the project for a working directory on <paramref name="machineId"/>, from facts when given, else from the path alone.</summary>
    /// <remarks>Saves any project it creates (see <see cref="ProjectService.ResolveAsync"/>), so call it before tracking other changes.</remarks>
    public async Task<(ProjectResolution Resolution, ProjectIdentity Identity)> ResolveAsync(
        Guid ownerId, Guid machineId, ProjectFacts? facts, string workingDirectory, CancellationToken cancellationToken)
    {
        var identity = facts is null ? ProjectIdentity.FromFolder(machineId, workingDirectory) : ProjectIdentity.From(machineId, facts);
        var resolution = await _projects.ResolveAsync(ownerId, identity, cancellationToken);
        return (resolution, identity);
    }

    /// <summary>Re-resolves the owner's filed transcripts that match <paramref name="which"/>, and moves the ones whose project changed.</summary>
    /// <returns>The projects transcripts moved out of.</returns>
    public async Task<IReadOnlyCollection<Guid>> RefileAsync(Guid ownerId, Expression<Func<Transcript, bool>> which, CancellationToken cancellationToken)
    {
        var groups = await _db.Transcripts.AsNoTracking()
            .Where(t => t.OwnerId == ownerId && t.ProjectFolder != null)
            .Where(which)
            .GroupBy(t => new { t.MachineId, t.ProjectFolder, t.ProjectFactsJson })
            .Select(g => g.Key)
            .ToListAsync(cancellationToken);

        var vacated = new HashSet<Guid>();
        foreach (var group in groups)
        {
            var facts = group.ProjectFactsJson is { } json ? ProjectFacts.FromJson(json) : null;
            var (resolution, _) = await ResolveAsync(ownerId, group.MachineId, facts, group.ProjectFolder!, cancellationToken);
            var moving = _db.Transcripts
                .Where(t => t.OwnerId == ownerId && t.MachineId == group.MachineId && t.ProjectFolder == group.ProjectFolder &&
                            t.ProjectFactsJson == group.ProjectFactsJson && t.ProjectId != resolution.ProjectId)
                .Where(which);
            vacated.UnionWith(await moving.Where(t => t.ProjectId != null).Select(t => t.ProjectId!.Value).Distinct().ToListAsync(cancellationToken));
            await moving.ExecuteUpdateAsync(set => set.SetProperty(t => t.ProjectId, resolution.ProjectId), cancellationToken);
        }

        return vacated;
    }

    /// <summary>Deletes the projects among <paramref name="projectIds"/> that no transcript is filed under and that only ever stood for a folder.</summary>
    /// <remarks>
    /// A shallow clone backed up before any full clone gets a machine-and-folder project; once it is refiled to the
    /// repo's project, that one is left empty and would otherwise linger in listings and suggestions.
    /// </remarks>
    public async Task ForgetEmptyFolderProjectsAsync(Guid ownerId, IReadOnlyCollection<Guid> projectIds, CancellationToken cancellationToken)
    {
        if (projectIds.Count == 0)
            return;
        var used = await _db.Transcripts
            .Where(t => t.OwnerId == ownerId && t.ProjectId != null && projectIds.Contains(t.ProjectId.Value))
            .Select(t => t.ProjectId!.Value)
            .Distinct()
            .ToListAsync(cancellationToken);
        await _projects.ForgetFolderProjectsAsync(ownerId, projectIds.Except(used).ToList(), cancellationToken);
    }
}

/// <summary>Refiles shallow clones that were filed by folder once a full clone attaches their remote.</summary>
/// <remarks>
/// ADR 0002: a shallow clone is keyed by machine and folder "until a full clone" is seen. Without this, a shallow
/// clone backed up before any full clone of the same repo would stay apart for good.
/// </remarks>
public sealed class RefileShallowClones : IDomainEventHandler<ProjectRemoteAttached>
{
    private readonly TranscriptProjects _transcripts;

    public RefileShallowClones(TranscriptProjects transcripts) => _transcripts = transcripts;

    public async Task HandleAsync(ProjectRemoteAttached domainEvent, CancellationToken cancellationToken)
    {
        var vacated = await _transcripts.RefileAsync(
            domainEvent.OwnerId,
            t => t.ProjectId != domainEvent.ProjectId && EF.Functions.JsonContains(t.ProjectFactsJson!, """{"shallow":true}"""),
            cancellationToken);
        await _transcripts.ForgetEmptyFolderProjectsAsync(domainEvent.OwnerId, vacated, cancellationToken);
    }
}

/// <summary>Moves a folder's transcripts when the user links or unlinks it (protostar-cli#57).</summary>
public sealed class RefileLinkedFolder : IDomainEventHandler<FolderLinkChanged>
{
    private readonly TranscriptProjects _transcripts;

    public RefileLinkedFolder(TranscriptProjects transcripts) => _transcripts = transcripts;

    public Task HandleAsync(FolderLinkChanged domainEvent, CancellationToken cancellationToken)
    {
        if (!ProjectIdentity.TryParseFolderKey(domainEvent.FolderKey, out var machineId, out var folder))
            return Task.CompletedTask;
        return _transcripts.RefileAsync(domainEvent.OwnerId, t => t.MachineId == machineId && t.ProjectFolder == folder, cancellationToken);
    }
}

/// <summary>Production <see cref="IFolderFilings"/>: reads which projects a folder's transcripts are filed under.</summary>
public sealed class TranscriptFolderFilings : IFolderFilings
{
    private readonly RegistryDbContext _db;

    public TranscriptFolderFilings(RegistryDbContext db) => _db = db;

    public async Task<IReadOnlyCollection<Guid>> ProjectsOfFolderAsync(Guid ownerId, ProjectIdentity identity, CancellationToken cancellationToken)
    {
        if (!ProjectIdentity.TryParseFolderKey(identity.FolderKey, out var machineId, out var folder))
            return [];
        return await _db.Transcripts.AsNoTracking()
            .Where(t => t.OwnerId == ownerId && t.MachineId == machineId && t.ProjectFolder == folder && t.ProjectId != null)
            .Select(t => t.ProjectId!.Value)
            .Distinct()
            .ToListAsync(cancellationToken);
    }
}
