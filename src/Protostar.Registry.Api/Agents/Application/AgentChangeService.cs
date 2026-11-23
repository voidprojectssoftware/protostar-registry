using Microsoft.EntityFrameworkCore;
using Npgsql;
using Protostar.Registry.Api.Infrastructure;

namespace Protostar.Registry.Api.Agents;

/// <summary>Application service for renaming and deleting an agent in the registry (registry#44, copied for agents by ADR 0003).</summary>
/// <remarks>
/// An agent is addressed like the reads address it: the caller's agent of that exact name in that project, where no
/// project means the personal agent. Another user's agent is never found, so it answers as missing rather than
/// forbidden; <see cref="Agent.Rename"/> and <see cref="Agent.EnsureCanDelete"/> still check the creator rule.
/// <para>
/// Both leave an <see cref="AgentTombstone"/> for the name that went away, so another machine's copy of it is not pushed
/// back (registry#58). The tombstones keep two rules: one exists only while its name has no live agent, so a rename
/// into a tombstoned name drops it; and a rename tombstone names the agent's current name, so a later rename moves
/// it along and a delete turns it into a delete.
/// </para>
/// </remarks>
public sealed class AgentChangeService
{
    private readonly RegistryDbContext _db;

    public AgentChangeService(RegistryDbContext db) => _db = db;

    /// <summary>Renames the caller's agent <paramref name="name"/> to <paramref name="newName"/>, keeping its version history.</summary>
    /// <param name="creatorId">The caller.</param>
    /// <param name="projectId">The agent's project; <c>null</c> for the personal agent.</param>
    /// <param name="name">The agent's current name.</param>
    /// <param name="newName">The name it takes.</param>
    /// <param name="cancellationToken">Cancels the rename.</param>
    public async Task<AgentChangeResult> RenameAsync(Guid creatorId, Guid? projectId, string name, string newName, CancellationToken cancellationToken)
    {
        var agent = await FindAsync(creatorId, projectId, name, cancellationToken);
        if (agent is null)
            return AgentChangeResult.Fail(AgentChangeError.NotFound, Missing(projectId, name));

        var namesInScope = await _db.Agents.AsNoTracking()
            .Where(s => s.CreatorId == creatorId && s.ProjectId == projectId)
            .Select(s => s.Name)
            .ToListAsync(cancellationToken);
        try
        {
            if (!agent.Rename(creatorId, newName, namesInScope))
                return AgentChangeResult.Ok(name);
        }
        catch (ArgumentException)
        {
            return AgentChangeResult.Fail(AgentChangeError.InvalidName, $"'{newName}' cannot name an agent: it must not be blank or contain '/' or '\\'.");
        }
        catch (AgentNameTakenException ex)
        {
            return AgentChangeResult.Fail(AgentChangeError.NameTaken, ex.Message);
        }

        var tombstones = await TombstonesAsync(creatorId, projectId, cancellationToken);
        _db.AgentTombstones.RemoveRange(tombstones.Where(t => t.Name == newName));
        foreach (var renamedHere in tombstones.Where(t => t.RenamedTo == name && t.Name != newName))
            renamedHere.FollowRename(newName);
        _db.AgentTombstones.Add(AgentTombstone.Renamed(agent, name, await LastVersionAsync(agent, cancellationToken), DateTimeOffset.UtcNow));

        try
        {
            await _db.SaveChangesRacingAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // A push of an agent with the new name landed between the check and the save; the unique index refused it.
            return AgentChangeResult.Fail(AgentChangeError.NameTaken, new AgentNameTakenException(newName).Message);
        }

        return AgentChangeResult.Ok(newName);
    }

    /// <summary>Deletes the caller's agent <paramref name="name"/> with every version and file it holds.</summary>
    /// <param name="creatorId">The caller.</param>
    /// <param name="projectId">The agent's project; <c>null</c> for the personal agent.</param>
    /// <param name="name">The agent's name.</param>
    /// <param name="cancellationToken">Cancels the delete.</param>
    /// <remarks>The versions are not loaded: the database cascades them from the agent row in the same statement.</remarks>
    public async Task<AgentChangeResult> DeleteAsync(Guid creatorId, Guid? projectId, string name, CancellationToken cancellationToken)
    {
        var agent = await FindAsync(creatorId, projectId, name, cancellationToken);
        if (agent is null)
            return AgentChangeResult.Fail(AgentChangeError.NotFound, Missing(projectId, name));

        agent.EnsureCanDelete(creatorId);
        foreach (var renamedHere in (await TombstonesAsync(creatorId, projectId, cancellationToken)).Where(t => t.RenamedTo == name))
            renamedHere.TargetDeleted();
        _db.AgentTombstones.Add(AgentTombstone.Deleted(agent, await LastVersionAsync(agent, cancellationToken), DateTimeOffset.UtcNow));
        _db.Agents.Remove(agent);

        try
        {
            await _db.SaveChangesRacingAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex is DbUpdateConcurrencyException || ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Another delete of the same agent removed it (or saved its tombstone) first; the agent is gone either way.
            return AgentChangeResult.Fail(AgentChangeError.NotFound, Missing(projectId, name));
        }

        return AgentChangeResult.Ok(name);
    }

    private Task<Agent?> FindAsync(Guid creatorId, Guid? projectId, string name, CancellationToken cancellationToken) =>
        _db.Agents.FirstOrDefaultAsync(s => s.CreatorId == creatorId && s.ProjectId == projectId && s.Name == name, cancellationToken);

    // Read rather than loaded with the agent: a tracked version would make EF order the agent's delete around the
    // agent's pointer to it. An agent always has a version once pushed; 0 is an agent row whose first push never saved.
    private async Task<int> LastVersionAsync(Agent agent, CancellationToken cancellationToken) =>
        await _db.AgentVersions.Where(v => v.AgentId == agent.Id).MaxAsync(v => (int?)v.VersionNumber, cancellationToken) ?? 0;

    private Task<List<AgentTombstone>> TombstonesAsync(Guid creatorId, Guid? projectId, CancellationToken cancellationToken) =>
        _db.AgentTombstones.Where(t => t.CreatorId == creatorId && t.ProjectId == projectId).ToListAsync(cancellationToken);

    private static string Missing(Guid? projectId, string name) =>
        projectId is { } p ? $"You have no agent '{name}' in project {p}." : $"You have no personal agent '{name}'.";
}

/// <summary>Why a rename or delete failed, so the endpoint can map it to a status code.</summary>
public enum AgentChangeError
{
    /// <summary>The caller has no agent of that name in that scope.</summary>
    NotFound,

    /// <summary>The new name fails <see cref="Agent.IsValidName"/>.</summary>
    InvalidName,

    /// <summary>Another agent in the same scope already has the new name.</summary>
    NameTaken,
}

/// <summary>The outcome of a rename or delete: the agent's name afterwards, or why nothing changed.</summary>
public sealed record AgentChangeResult(string? Name, AgentChangeError? Error, string? Message)
{
    public static AgentChangeResult Ok(string name) => new(name, null, null);

    public static AgentChangeResult Fail(AgentChangeError error, string message) => new(null, error, message);
}
