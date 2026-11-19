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
        var folderProjects = await _db.Transcripts.AsNoTracking()
            .Where(t => t.OwnerId == ownerId && t.MachineId == machineId && t.ProjectFolder == identity.Folder && t.ProjectId != null)
            .Select(t => t.ProjectId!.Value)
            .Distinct()
            .ToListAsync(cancellationToken);
        var resolution = await _projects.ResolveAsync(ownerId, identity, folderProjects, cancellationToken);
        return (resolution, identity);
    }

    /// <summary>Re-resolves the owner's filed transcripts that match <paramref name="which"/>, and moves the ones whose project changed.</summary>
    /// <returns>How many transcripts moved.</returns>
    public async Task<int> RefileAsync(Guid ownerId, Expression<Func<Transcript, bool>> which, CancellationToken cancellationToken)
    {
        var groups = await _db.Transcripts.AsNoTracking()
            .Where(t => t.OwnerId == ownerId && t.ProjectFolder != null)
            .Where(which)
            .GroupBy(t => new { t.MachineId, t.ProjectFolder, t.ProjectFactsJson })
            .Select(g => g.Key)
            .ToListAsync(cancellationToken);

        var moved = 0;
        foreach (var group in groups)
        {
            var facts = group.ProjectFactsJson is { } json ? ProjectFacts.FromJson(json) : null;
            var (resolution, identity) = await ResolveAsync(ownerId, group.MachineId, facts, group.ProjectFolder!, cancellationToken);
            moved += await _db.Transcripts
                .Where(t => t.OwnerId == ownerId && t.MachineId == group.MachineId && t.ProjectFolder == group.ProjectFolder &&
                            t.ProjectFactsJson == group.ProjectFactsJson && t.ProjectId != resolution.ProjectId)
                .Where(which)
                .ExecuteUpdateAsync(set => set.SetProperty(t => t.ProjectId, resolution.ProjectId), cancellationToken);
        }

        return moved;
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

    public Task HandleAsync(ProjectRemoteAttached domainEvent, CancellationToken cancellationToken) =>
        _transcripts.RefileAsync(
            domainEvent.OwnerId,
            t => t.ProjectId != domainEvent.ProjectId && EF.Functions.JsonContains(t.ProjectFactsJson!, """{"shallow":true}"""),
            cancellationToken);
}
