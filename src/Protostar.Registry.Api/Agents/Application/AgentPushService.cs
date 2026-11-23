using System.Text;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Protostar.Registry.Api.Common;
using Protostar.Registry.Api.Infrastructure;
using Protostar.Registry.Api.Projects;
using Protostar.Registry.Api.Skills;

namespace Protostar.Registry.Api.Agents;

/// <summary>Application service for "push a local agent": validates the one file, loads or creates the <see cref="Agent"/>, and lets it decide.</summary>
/// <remarks>
/// A copy of <see cref="SkillPushService"/> for a single file (ADR 0003), with the same rules: a push carrying a
/// project folder's facts is a project agent resolved by <see cref="ProjectService"/>, otherwise a personal one; a push
/// based on a version that is not current is refused as stale; a push from a copy of a removed agent is refused; two
/// racing pushes are decided by the unique (agent, version) index. The name comes from the file's front matter alone:
/// there is no fallback name, because Claude Code identifies an agent by that field and would not load a file without it.
/// </remarks>
public sealed class AgentPushService
{
    private readonly RegistryDbContext _db;
    private readonly IDomainEventDispatcher _dispatcher;
    private readonly ProjectService _projects;

    public AgentPushService(RegistryDbContext db, IDomainEventDispatcher dispatcher, ProjectService projects)
    {
        _db = db;
        _dispatcher = dispatcher;
        _projects = projects;
    }

    /// <summary>Pushes one agent file for <paramref name="creatorId"/> (the authenticated caller).</summary>
    /// <param name="creatorId">The authenticated caller.</param>
    /// <param name="project">The project folder a project agent lives in; <c>null</c> for a personal agent.</param>
    /// <param name="relativePath">The file's path relative to the agents folder.</param>
    /// <param name="content">The file's bytes.</param>
    /// <param name="baseVersion">The version the pusher's copy was based on (0 for none); <c>null</c> when it does not say.</param>
    /// <param name="cancellationToken">Cancels the push.</param>
    public async Task<AgentPushResult> PushAsync(
        Guid creatorId, ProjectFolderRequest? project, string relativePath, byte[] content, int? baseVersion, CancellationToken cancellationToken)
    {
        if (!RelativePath.TryCreate(relativePath, out var path))
            return AgentPushResult.Failed(null, AgentPushError.InvalidPath, $"Invalid file path: '{relativePath}'.");

        var (manifest, problem) = AgentManifestParser.Parse(Encoding.UTF8.GetString(content));
        if (manifest is null)
            return AgentPushResult.Failed(null, AgentPushError.InvalidManifest, problem!);
        var name = manifest.Name;
        if (!Agent.IsValidName(name))
            return AgentPushResult.Failed(name, AgentPushError.InvalidName, $"The agent name '{name}' cannot contain '/' or '\\'.");

        if (project?.Facts.Problem() is { } badFacts)
            return AgentPushResult.Failed(name, AgentPushError.InvalidProject, badFacts);

        if (await _db.Users.FindAsync([creatorId], cancellationToken) is null)
            return AgentPushResult.Failed(name, AgentPushError.UnknownCreator, "The authenticated user no longer exists.");

        // Resolve first: resolution saves any new project and may reset the change tracker on a race.
        var resolution = project is null
            ? null
            : await _projects.ResolveAsync(creatorId, ProjectIdentity.From(project.MachineId, project.Facts), cancellationToken);
        var projectId = resolution?.ProjectId;

        var agent = await _db.Agents
            .Include(a => a.CurrentVersion)
            .FirstOrDefaultAsync(a => a.CreatorId == creatorId && a.ProjectId == projectId && a.Name == name, cancellationToken);

        var now = DateTimeOffset.UtcNow;
        var isNew = agent is null;
        AgentTombstone? tombstone = null;
        if (isNew)
        {
            tombstone = await _db.AgentTombstones.AsNoTracking()
                .FirstOrDefaultAsync(t => t.CreatorId == creatorId && t.ProjectId == projectId && t.Name == name, cancellationToken);
            if (tombstone?.Refuses(baseVersion) == true)
                return AgentPushResult.Removed(tombstone, baseVersion!.Value, resolution);
        }

        agent ??= Agent.Create(creatorId, name, now, projectId, tombstone);

        AgentVersionPushOutcome outcome;
        try
        {
            outcome = agent.PushVersion(creatorId, manifest, path, content, now, baseVersion);
        }
        catch (AgentVersionStaleException exception)
        {
            return AgentPushResult.Stale(name, exception.CurrentVersion, exception.Message, resolution);
        }
        catch (AgentCreatorViolationException)
        {
            return AgentPushResult.Failed(name, AgentPushError.NotCreator, "Only an agent's creator can push new versions of it.");
        }
        catch (AgentSizeExceededException exception)
        {
            return AgentPushResult.Failed(name, AgentPushError.TooLarge, exception.Message);
        }

        if (outcome.IsUnchanged)
            return AgentPushResult.Stored(AgentPushStatus.Unchanged, name, outcome.Version, agent.Id, resolution);

        try
        {
            await PersistAsync(agent, outcome.Version, isNew, tombstone, cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return await LostRaceAsync(creatorId, projectId, name, outcome.Version, baseVersion, resolution, cancellationToken);
        }

        return AgentPushResult.Stored(AgentPushStatus.Created, name, outcome.Version, agent.Id, resolution);
    }

    /// <summary>The result for a push whose save lost a race with another push of the same agent.</summary>
    /// <remarks>Identical content is the same edit arriving twice, so unchanged; anything else is stale. As <see cref="SkillPushService"/>.</remarks>
    private async Task<AgentPushResult> LostRaceAsync(
        Guid creatorId, Guid? projectId, string name, AgentVersion lost, int? baseVersion, ProjectResolution? resolution, CancellationToken cancellationToken)
    {
        _db.ChangeTracker.Clear();
        var winner = await _db.Agents.AsNoTracking()
            .Where(a => a.CreatorId == creatorId && a.ProjectId == projectId && a.Name == name && a.CurrentVersion != null)
            .Select(a => new { a.Id, Version = a.CurrentVersion! })
            .FirstAsync(cancellationToken);

        if (winner.Version.ContentHash == lost.ContentHash)
            return AgentPushResult.Stored(AgentPushStatus.Unchanged, name, winner.Version, winner.Id, resolution);

        var based = baseVersion ?? lost.VersionNumber - 1;
        return AgentPushResult.Stale(name, winner.Version.VersionNumber, new AgentVersionStaleException(based, winner.Version.VersionNumber).Message, resolution);
    }

    /// <summary>Persists a newly pushed version and dispatches its event.</summary>
    /// <remarks>
    /// A new agent and its first version form a foreign-key cycle (the agent points at its current version, the version
    /// at its agent), so the agent is inserted with the pointer null and the pointer set in a second save, in one
    /// transaction, exactly as <see cref="SkillPushService"/> does. A new agent taking a removed agent's name drops that
    /// name's tombstone in the same transaction.
    /// </remarks>
    private async Task PersistAsync(Agent agent, AgentVersion newVersion, bool isNew, AgentTombstone? replaced, CancellationToken cancellationToken)
    {
        if (!isNew)
        {
            _db.Add(newVersion);
            await _db.SaveChangesRacingAndDispatchAsync(_dispatcher, cancellationToken);
            return;
        }

        var strategy = _db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);

            if (replaced is not null)
                await _db.AgentTombstones.Where(t => t.Id == replaced.Id).ExecuteDeleteAsync(cancellationToken);

            _db.Agents.Add(agent);
            var pointer = _db.Entry(agent).Property(a => a.CurrentVersionId);
            var currentVersionId = pointer.CurrentValue;
            pointer.CurrentValue = null;

            await _db.SaveChangesRacingAsync(cancellationToken);

            pointer.CurrentValue = currentVersionId;
            await _db.SaveChangesRacingAndDispatchAsync(_dispatcher, cancellationToken);

            await transaction.CommitAsync(cancellationToken);
        });
    }
}
