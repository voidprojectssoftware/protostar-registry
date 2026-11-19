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
/// </remarks>
public sealed class SkillQueryService
{
    private readonly RegistryDbContext _db;

    public SkillQueryService(RegistryDbContext db) => _db = db;

    /// <summary>Every skill <paramref name="creatorId"/> has pushed, with its current version, ordered by name.</summary>
    public async Task<IReadOnlyList<SkillSummary>> ListAsync(Guid creatorId, CancellationToken cancellationToken)
    {
        var rows = await _db.Skills
            .AsNoTracking()
            .Where(s => s.CreatorId == creatorId && s.CurrentVersion != null)
            .Select(s => new
            {
                s.Name,
                s.CurrentVersion!.Description,
                s.CurrentVersion.VersionNumber,
                s.CurrentVersion.ContentHash,
                s.CurrentVersion.CreatedAt,
                FileCount = s.CurrentVersion.Files.Count,
                TotalBytes = s.CurrentVersion.Files.Sum(f => f.Size),
            })
            .ToListAsync(cancellationToken);

        return rows
            .Select(r => new SkillSummary(r.Name, r.Description, r.VersionNumber, r.ContentHash.Value, r.CreatedAt, r.FileCount, r.TotalBytes))
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// One version of the skill named <paramref name="name"/> with every file's bytes, or <c>null</c> when
    /// the caller has no such skill or the skill has no such version.
    /// </summary>
    /// <param name="creatorId">The caller; only their skills are visible.</param>
    /// <param name="name">The skill's exact name, as <see cref="ListAsync"/> reports it.</param>
    /// <param name="version">The version number to fetch; <c>null</c> for the current version.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    public async Task<SkillVersionContent?> GetContentAsync(
        Guid creatorId, string name, int? version, CancellationToken cancellationToken)
    {
        var skill = await _db.Skills
            .AsNoTracking()
            .Where(s => s.CreatorId == creatorId && s.Name == name)
            .Select(s => new { s.Id, s.Name, s.CurrentVersionId })
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

        return new SkillVersionContent(skill.Name, found.VersionNumber, found.Description, found.ContentHash.Value, found.CreatedAt, files);
    }
}

/// <summary>One skill in a creator's list, described by its current version.</summary>
/// <param name="Name">The skill's name, unique per creator.</param>
/// <param name="Description">The current version's SKILL.md description; <c>null</c> when omitted.</param>
/// <param name="Version">The current version number.</param>
/// <param name="ContentHash">The current version's content hash (see <see cref="Sha256Hash.OfFiles"/>).</param>
/// <param name="UpdatedAt">When the current version was pushed.</param>
/// <param name="FileCount">How many files the current version holds.</param>
/// <param name="TotalBytes">The current version's total size in bytes.</param>
public sealed record SkillSummary(
    string Name, string? Description, int Version, string ContentHash, DateTimeOffset UpdatedAt, int FileCount, long TotalBytes);

/// <summary>One version of a skill with every file's bytes.</summary>
/// <param name="Name">The skill's name, exactly as stored.</param>
/// <param name="Version">The version number returned.</param>
/// <param name="Description">That version's SKILL.md description; <c>null</c> when omitted.</param>
/// <param name="ContentHash">That version's content hash (see <see cref="Sha256Hash.OfFiles"/>).</param>
/// <param name="PushedAt">When that version was pushed.</param>
/// <param name="Files">Every file of the version, ordered by path (ordinal).</param>
public sealed record SkillVersionContent(
    string Name, int Version, string? Description, string ContentHash, DateTimeOffset PushedAt, IReadOnlyList<SkillFileData> Files);

/// <summary>One file of a fetched skill version.</summary>
/// <param name="Path">The path relative to the skill root, with <c>/</c> separators.</param>
/// <param name="Size">The file size in bytes.</param>
/// <param name="Sha256">The uppercase hex SHA-256 of <paramref name="Content"/>.</param>
/// <param name="Content">The file's bytes, verbatim.</param>
public sealed record SkillFileData(string Path, long Size, string Sha256, byte[] Content);
