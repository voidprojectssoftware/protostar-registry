using Microsoft.EntityFrameworkCore;
using Protostar.Registry.Api.Infrastructure;

namespace Protostar.Registry.Api.Skills;

/// <summary>
/// Application service for the read side of skills: listing a creator's skills and fetching one version's
/// files so a machine can install it.
/// </summary>
/// <remarks>
/// Reads change no state, so they bypass the aggregate and project straight from the store without
/// tracking. Every query is scoped to the caller as creator: a user only ever sees the skills they pushed.
/// A skill is addressed by its name and its project, where no project means the global skill.
/// </remarks>
public sealed class SkillQueryService
{
    private readonly RegistryDbContext _db;

    public SkillQueryService(RegistryDbContext db) => _db = db;

    /// <summary>The skills <paramref name="creatorId"/> has pushed that <paramref name="filter"/> selects, with their current versions, ordered by name.</summary>
    public async Task<IReadOnlyList<SkillSummary>> ListAsync(Guid creatorId, SkillListFilter filter, CancellationToken cancellationToken)
    {
        var query = _db.Skills.AsNoTracking().Where(s => s.CreatorId == creatorId && s.CurrentVersion != null);
        query = filter switch
        {
            { ProjectId: { } projectId } => query.Where(s => s.ProjectId == projectId),
            { IncludeProjects: true } => query,
            _ => query.Where(s => s.ProjectId == null),
        };

        var rows = await query
            .Select(s => new
            {
                s.Name,
                s.ProjectId,
                ProjectName = _db.Projects.Where(p => p.Id == s.ProjectId).Select(p => p.Name).FirstOrDefault(),
                s.CurrentVersion!.Description,
                s.CurrentVersion.VersionNumber,
                s.CurrentVersion.ContentHash,
                s.CurrentVersion.CreatedAt,
                FileCount = s.CurrentVersion.Files.Count,
                TotalBytes = s.CurrentVersion.Files.Sum(f => f.Size),
            })
            .ToListAsync(cancellationToken);

        return rows
            .Select(r => new SkillSummary(r.Name, r.Description, r.VersionNumber, r.ContentHash.Value, r.CreatedAt, r.FileCount, r.TotalBytes, r.ProjectId, r.ProjectName))
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.ProjectName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>The names <paramref name="creatorId"/> deleted or renamed away that <paramref name="filter"/> selects, ordered by name.</summary>
    /// <remarks>Listed with the skills so a client can tell a copy of a removed skill from a skill the registry never had, at no extra request.</remarks>
    public async Task<IReadOnlyList<SkillRemoval>> ListRemovedAsync(Guid creatorId, SkillListFilter filter, CancellationToken cancellationToken)
    {
        var query = _db.SkillTombstones.AsNoTracking().Where(t => t.CreatorId == creatorId);
        query = filter switch
        {
            { ProjectId: { } projectId } => query.Where(t => t.ProjectId == projectId),
            { IncludeProjects: true } => query,
            _ => query.Where(t => t.ProjectId == null),
        };

        var rows = await query.Select(t => new SkillRemoval(t.Name, t.ProjectId, t.LastVersion, t.RenamedTo, t.RemovedAt)).ToListAsync(cancellationToken);
        return rows.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// One version of the skill named <paramref name="name"/> with every file's bytes, or <c>null</c> when
    /// the caller has no such skill or the skill has no such version.
    /// </summary>
    /// <param name="creatorId">The caller; only their skills are visible.</param>
    /// <param name="projectId">The skill's project; <c>null</c> for the global skill of that name.</param>
    /// <param name="name">The skill's exact name, as <see cref="ListAsync"/> reports it.</param>
    /// <param name="version">The version number to fetch; <c>null</c> for the current version.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    public async Task<SkillVersionContent?> GetContentAsync(
        Guid creatorId, Guid? projectId, string name, int? version, CancellationToken cancellationToken)
    {
        var skill = await _db.Skills
            .AsNoTracking()
            .Where(s => s.CreatorId == creatorId && s.ProjectId == projectId && s.Name == name)
            .Select(s => new { s.Id, s.Name, s.ProjectId, s.CurrentVersionId })
            .FirstOrDefaultAsync(cancellationToken);
        if (skill is null)
            return null;

        var query = _db.SkillVersions.AsNoTracking().Include(v => v.Files).Where(v => v.SkillId == skill.Id);
        query = version is { } number
            ? query.Where(v => v.VersionNumber == number)
            : query.Where(v => v.Id == skill.CurrentVersionId);

        // At most one row matches (a version number is unique within a skill), so a single query with the
        // files joined in is enough: no split query, and no ordering is needed for FirstOrDefault.
        var found = await query.FirstOrDefaultAsync(cancellationToken);
        if (found is null)
            return null;

        var files = found.Files
            .OrderBy(f => f.RelativePath.Value, StringComparer.Ordinal)
            .Select(f => new SkillFileData(f.RelativePath.Value, f.Size, f.Sha256.Value, f.Content))
            .ToList();

        return new SkillVersionContent(skill.Name, skill.ProjectId, found.VersionNumber, found.Description, found.ContentHash.Value, found.CreatedAt, files);
    }
}

/// <summary>Which of a creator's skills to list.</summary>
/// <param name="IncludeProjects">List every project's skills as well as the global ones.</param>
/// <param name="ProjectId">List only this project's skills; wins over <paramref name="IncludeProjects"/>.</param>
/// <remarks>The default lists only global skills, which is all a client from before project skills understands.</remarks>
public sealed record SkillListFilter(bool IncludeProjects = false, Guid? ProjectId = null);

/// <summary>One skill in a creator's list, described by its current version.</summary>
/// <param name="Name">The skill's name, unique per creator and project.</param>
/// <param name="Description">The current version's SKILL.md description; <c>null</c> when omitted.</param>
/// <param name="Version">The current version number.</param>
/// <param name="ContentHash">The current version's content hash (see <see cref="Sha256Hash.OfFiles"/>).</param>
/// <param name="UpdatedAt">When the current version was pushed.</param>
/// <param name="FileCount">How many files the current version holds.</param>
/// <param name="TotalBytes">The current version's total size in bytes.</param>
/// <param name="ProjectId">The skill's project; <c>null</c> for a global skill.</param>
/// <param name="ProjectName">That project's display name; <c>null</c> for a global skill.</param>
public sealed record SkillSummary(
    string Name, string? Description, int Version, string ContentHash, DateTimeOffset UpdatedAt, int FileCount, long TotalBytes,
    Guid? ProjectId = null, string? ProjectName = null);

/// <summary>A skill name the creator deleted or renamed away (a <see cref="SkillTombstone"/>).</summary>
/// <param name="Name">The removed name.</param>
/// <param name="ProjectId">Its project; <c>null</c> for a global skill.</param>
/// <param name="LastVersion">The skill's last version under this name.</param>
/// <param name="RenamedTo">The skill's name now, when it was renamed; <c>null</c> when it was deleted.</param>
/// <param name="RemovedAt">When the name was removed.</param>
public sealed record SkillRemoval(string Name, Guid? ProjectId, int LastVersion, string? RenamedTo, DateTimeOffset RemovedAt);

/// <summary>One version of a skill with every file's bytes.</summary>
/// <param name="Name">The skill's name, exactly as stored.</param>
/// <param name="ProjectId">The skill's project; <c>null</c> for a global skill.</param>
/// <param name="Version">The version number returned.</param>
/// <param name="Description">That version's SKILL.md description; <c>null</c> when omitted.</param>
/// <param name="ContentHash">That version's content hash (see <see cref="Sha256Hash.OfFiles"/>).</param>
/// <param name="PushedAt">When that version was pushed.</param>
/// <param name="Files">Every file of the version, ordered by path (ordinal).</param>
public sealed record SkillVersionContent(
    string Name, Guid? ProjectId, int Version, string? Description, string ContentHash, DateTimeOffset PushedAt, IReadOnlyList<SkillFileData> Files);

/// <summary>One file of a fetched skill version.</summary>
/// <param name="Path">The path relative to the skill root, with <c>/</c> separators.</param>
/// <param name="Size">The file size in bytes.</param>
/// <param name="Sha256">The uppercase hex SHA-256 of <paramref name="Content"/>.</param>
/// <param name="Content">The file's bytes, verbatim.</param>
public sealed record SkillFileData(string Path, long Size, string Sha256, byte[] Content);
