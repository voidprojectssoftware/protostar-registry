using Microsoft.EntityFrameworkCore;
using Protostar.Registry.Api.Infrastructure;

namespace Protostar.Registry.Api.Agents;

/// <summary>Application service for the read side of agents: listing a creator's agents and the names they removed.</summary>
/// <remarks>Reads bypass the aggregate and project straight from the store. Every query is scoped to the caller as creator.</remarks>
public sealed class AgentQueryService
{
    private readonly RegistryDbContext _db;

    public AgentQueryService(RegistryDbContext db) => _db = db;

    /// <summary>The agents <paramref name="creatorId"/> has pushed that <paramref name="filter"/> selects, with their current versions, ordered by name.</summary>
    public async Task<IReadOnlyList<AgentSummary>> ListAsync(Guid creatorId, AgentListFilter filter, CancellationToken cancellationToken)
    {
        var query = _db.Agents.AsNoTracking().Where(a => a.CreatorId == creatorId && a.CurrentVersion != null);
        query = filter switch
        {
            { ProjectId: { } projectId } => query.Where(a => a.ProjectId == projectId),
            { IncludeProjects: true } => query,
            _ => query.Where(a => a.ProjectId == null),
        };

        var rows = await query
            .Select(a => new
            {
                a.Name,
                a.ProjectId,
                ProjectName = _db.Projects.Where(p => p.Id == a.ProjectId).Select(p => p.Name).FirstOrDefault(),
                a.CurrentVersion!.Description,
                a.CurrentVersion.Path,
                a.CurrentVersion.VersionNumber,
                a.CurrentVersion.ContentHash,
                a.CurrentVersion.CreatedAt,
                a.CurrentVersion.Size,
                FirstVersion = _db.AgentVersions.Where(v => v.AgentId == a.Id).Min(v => v.VersionNumber),
            })
            .ToListAsync(cancellationToken);

        return rows
            .Select(r => new AgentSummary(r.Name, r.Description, r.Path.Value, r.VersionNumber, r.ContentHash.Value, r.CreatedAt, r.Size, r.ProjectId, r.ProjectName, r.FirstVersion))
            .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(a => a.ProjectName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>The names <paramref name="creatorId"/> deleted or renamed away that <paramref name="filter"/> selects, ordered by name.</summary>
    public async Task<IReadOnlyList<AgentRemoval>> ListRemovedAsync(Guid creatorId, AgentListFilter filter, CancellationToken cancellationToken)
    {
        var query = _db.AgentTombstones.AsNoTracking().Where(t => t.CreatorId == creatorId);
        query = filter switch
        {
            { ProjectId: { } projectId } => query.Where(t => t.ProjectId == projectId),
            { IncludeProjects: true } => query,
            _ => query.Where(t => t.ProjectId == null),
        };

        var rows = await query.Select(t => new AgentRemoval(t.Name, t.ProjectId, t.LastVersion, t.RenamedTo, t.RemovedAt)).ToListAsync(cancellationToken);
        return rows.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }
}

/// <summary>Which of a creator's agents to list.</summary>
/// <param name="IncludeProjects">List every project's agents as well as the personal ones.</param>
/// <param name="ProjectId">List only this project's agents; wins over <paramref name="IncludeProjects"/>.</param>
public sealed record AgentListFilter(bool IncludeProjects = false, Guid? ProjectId = null);

/// <summary>One agent in a creator's list, described by its current version.</summary>
/// <param name="Name">The agent's name, unique per creator and project.</param>
/// <param name="Description">The current version's description.</param>
/// <param name="Path">The path the current version was pushed from, relative to the agents folder.</param>
/// <param name="Version">The current version number.</param>
/// <param name="ContentHash">SHA-256 of the current version's file, uppercase hex.</param>
/// <param name="UpdatedAt">When the current version was pushed.</param>
/// <param name="Size">The current version's size in bytes.</param>
/// <param name="ProjectId">The agent's project; <c>null</c> for a personal agent.</param>
/// <param name="ProjectName">That project's display name; <c>null</c> for a personal agent.</param>
/// <param name="FirstVersion">1, or above it for an agent that reused a removed agent's name (<see cref="AgentTombstone"/>).</param>
public sealed record AgentSummary(
    string Name, string Description, string Path, int Version, string ContentHash, DateTimeOffset UpdatedAt, long Size,
    Guid? ProjectId, string? ProjectName, int FirstVersion);

/// <summary>An agent name the creator deleted or renamed away (an <see cref="AgentTombstone"/>).</summary>
public sealed record AgentRemoval(string Name, Guid? ProjectId, int LastVersion, string? RenamedTo, DateTimeOffset RemovedAt);
