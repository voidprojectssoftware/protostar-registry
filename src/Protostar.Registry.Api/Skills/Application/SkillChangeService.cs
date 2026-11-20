using Microsoft.EntityFrameworkCore;
using Npgsql;
using Protostar.Registry.Api.Infrastructure;

namespace Protostar.Registry.Api.Skills;

/// <summary>Application service for renaming and deleting a skill in the registry (registry#44).</summary>
/// <remarks>
/// A skill is addressed like the reads address it: the caller's skill of that exact name in that project, where no
/// project means the global skill. Another user's skill is never found, so it answers as missing rather than
/// forbidden; <see cref="Skill.Rename"/> and <see cref="Skill.EnsureCanDelete"/> still check the creator rule.
/// </remarks>
public sealed class SkillChangeService
{
    private readonly RegistryDbContext _db;

    public SkillChangeService(RegistryDbContext db) => _db = db;

    /// <summary>Renames the caller's skill <paramref name="name"/> to <paramref name="newName"/>, keeping its version history.</summary>
    /// <param name="creatorId">The caller.</param>
    /// <param name="projectId">The skill's project; <c>null</c> for the global skill.</param>
    /// <param name="name">The skill's current name.</param>
    /// <param name="newName">The name it takes.</param>
    /// <param name="cancellationToken">Cancels the rename.</param>
    public async Task<SkillChangeResult> RenameAsync(Guid creatorId, Guid? projectId, string name, string newName, CancellationToken cancellationToken)
    {
        var skill = await FindAsync(creatorId, projectId, name, cancellationToken);
        if (skill is null)
            return SkillChangeResult.Fail(SkillChangeError.NotFound, Missing(projectId, name));

        var namesInScope = await _db.Skills.AsNoTracking()
            .Where(s => s.CreatorId == creatorId && s.ProjectId == projectId)
            .Select(s => s.Name)
            .ToListAsync(cancellationToken);
        try
        {
            if (!skill.Rename(creatorId, newName, namesInScope))
                return SkillChangeResult.Ok(name);
        }
        catch (ArgumentException)
        {
            return SkillChangeResult.Fail(SkillChangeError.InvalidName, $"'{newName}' cannot name a skill: it must not be blank or contain '/' or '\\'.");
        }
        catch (SkillNameTakenException ex)
        {
            return SkillChangeResult.Fail(SkillChangeError.NameTaken, ex.Message);
        }

        try
        {
            await _db.SaveChangesRacingAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // A push of a skill with the new name landed between the check and the save; the unique index refused it.
            return SkillChangeResult.Fail(SkillChangeError.NameTaken, new SkillNameTakenException(newName).Message);
        }

        return SkillChangeResult.Ok(newName);
    }

    /// <summary>Deletes the caller's skill <paramref name="name"/> with every version and file it holds.</summary>
    /// <param name="creatorId">The caller.</param>
    /// <param name="projectId">The skill's project; <c>null</c> for the global skill.</param>
    /// <param name="name">The skill's name.</param>
    /// <param name="cancellationToken">Cancels the delete.</param>
    /// <remarks>The versions and files are not loaded: the database cascades them from the skill row in the same statement.</remarks>
    public async Task<SkillChangeResult> DeleteAsync(Guid creatorId, Guid? projectId, string name, CancellationToken cancellationToken)
    {
        var skill = await FindAsync(creatorId, projectId, name, cancellationToken);
        if (skill is null)
            return SkillChangeResult.Fail(SkillChangeError.NotFound, Missing(projectId, name));

        skill.EnsureCanDelete(creatorId);
        _db.Skills.Remove(skill);
        await _db.SaveChangesAsync(cancellationToken);
        return SkillChangeResult.Ok(name);
    }

    private Task<Skill?> FindAsync(Guid creatorId, Guid? projectId, string name, CancellationToken cancellationToken) =>
        _db.Skills.FirstOrDefaultAsync(s => s.CreatorId == creatorId && s.ProjectId == projectId && s.Name == name, cancellationToken);

    private static string Missing(Guid? projectId, string name) =>
        projectId is { } p ? $"You have no skill '{name}' in project {p}." : $"You have no global skill '{name}'.";
}

/// <summary>Why a rename or delete failed, so the endpoint can map it to a status code.</summary>
public enum SkillChangeError
{
    /// <summary>The caller has no skill of that name in that scope.</summary>
    NotFound,

    /// <summary>The new name fails <see cref="Skill.IsValidName"/>.</summary>
    InvalidName,

    /// <summary>Another skill in the same scope already has the new name.</summary>
    NameTaken,
}

/// <summary>The outcome of a rename or delete: the skill's name afterwards, or why nothing changed.</summary>
public sealed record SkillChangeResult(string? Name, SkillChangeError? Error, string? Message)
{
    public static SkillChangeResult Ok(string name) => new(name, null, null);

    public static SkillChangeResult Fail(SkillChangeError error, string message) => new(null, error, message);
}
