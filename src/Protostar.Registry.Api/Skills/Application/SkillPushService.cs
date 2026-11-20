using System.Text;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Protostar.Registry.Api.Common;
using Protostar.Registry.Api.Infrastructure;
using Protostar.Registry.Api.Projects;

namespace Protostar.Registry.Api.Skills;

/// <summary>
/// Application service for "push a local skill": it turns an upload into domain inputs (validated file
/// contents and a parsed manifest), loads or creates the <see cref="Skill"/> aggregate, and delegates the
/// version rules to <see cref="Skill.PushVersion"/>. The domain owns the invariants; this service owns the
/// transaction, the lookup, and translating outcomes into a transport-friendly <see cref="SkillPushResult"/>.
/// A push whose content matches the skill's current version is a no-op. Shared by the single and bulk
/// endpoints so neither re-implements validation, naming, or version bookkeeping.
/// </summary>
/// <remarks>
/// A push that carries a project folder's facts is a project skill: the facts are resolved to a project by
/// <see cref="ProjectService"/>, the same matching that files sessions, and the skill is looked up by
/// (creator, project, name). A push without them is a global skill, which is also what every client from
/// before project skills sends.
/// A push may name the version its copy was based on. When that is not the current version the push is refused
/// as stale (<see cref="SkillPushError.StaleVersion"/>) rather than stored on top; a push that names none is
/// accepted as before, for clients from before the rule. Two pushes racing from the same base both pass the
/// aggregate's check, and the unique (skill, version) index saves only one: the other is reported as stale, or
/// as unchanged when it carried the same content as the winner.
/// </remarks>
public sealed class SkillPushService
{
    private readonly RegistryDbContext _db;
    private readonly IDomainEventDispatcher _dispatcher;
    private readonly ProjectService _projects;

    public SkillPushService(RegistryDbContext db, IDomainEventDispatcher dispatcher, ProjectService projects)
    {
        _db = db;
        _dispatcher = dispatcher;
        _projects = projects;
    }

    /// <summary>
    /// Pushes one skill for <paramref name="creatorId"/> (the authenticated caller).
    /// <paramref name="fallbackName"/> names the skill when its SKILL.md omits a name (the bulk endpoint
    /// passes the skill's directory segment). Each call commits independently so one bad skill in a batch
    /// doesn't roll back the others.
    /// </summary>
    /// <param name="creatorId">The authenticated caller.</param>
    /// <param name="project">The project folder a project skill lives in; <c>null</c> for a global skill.</param>
    /// <param name="fallbackName">The name to use when the SKILL.md declares none.</param>
    /// <param name="uploads">The skill's files, relative to its root.</param>
    /// <param name="baseVersion">The version the pusher's copy was based on (0 for none); <c>null</c> when it does not say.</param>
    /// <param name="cancellationToken">Cancels the push.</param>
    public async Task<SkillPushResult> PushAsync(
        Guid creatorId,
        ProjectFolderRequest? project,
        string? fallbackName,
        IReadOnlyList<SkillFileUpload> uploads,
        int? baseVersion,
        CancellationToken cancellationToken)
    {
        if (uploads.Count == 0)
            return SkillPushResult.Failed(fallbackName, SkillPushError.NoFiles, "The skill upload contained no files.");

        var files = new List<SkillFileContent>(uploads.Count);
        foreach (var upload in uploads)
        {
            if (!RelativePath.TryCreate(upload.RelativePath, out var path))
                return SkillPushResult.Failed(fallbackName, SkillPushError.InvalidPath, $"Invalid file path: '{upload.RelativePath}'.");

            files.Add(SkillFileContent.Create(path, upload.Content));
        }

        if (files.Select(f => f.Path.Value).Distinct(StringComparer.OrdinalIgnoreCase).Count() != files.Count)
            return SkillPushResult.Failed(fallbackName, SkillPushError.InvalidPath, "The skill contained duplicate file paths.");

        var manifestFile = files.FirstOrDefault(f => f.Path.Value.Equals("SKILL.md", StringComparison.OrdinalIgnoreCase));
        if (manifestFile is null)
            return SkillPushResult.Failed(fallbackName, SkillPushError.MissingSkillManifest, "The skill is missing a SKILL.md at its root.");

        var manifest = SkillManifestParser.Parse(Encoding.UTF8.GetString(manifestFile.Bytes)) ?? SkillManifest.Empty;
        var name = Blank(manifest.Name) ?? Blank(fallbackName);
        if (name is null)
            return SkillPushResult.Failed(fallbackName, SkillPushError.MissingName, "The skill declares no name in its SKILL.md and none was supplied.");
        if (!Skill.IsValidName(name))
            return SkillPushResult.Failed(name, SkillPushError.InvalidName, $"The skill name '{name}' cannot contain '/' or '\\'.");

        if (project?.Facts.Problem() is { } badFacts)
            return SkillPushResult.Failed(name, SkillPushError.InvalidProject, badFacts);

        if (await _db.Users.FindAsync([creatorId], cancellationToken) is null)
            return SkillPushResult.Failed(name, SkillPushError.UnknownCreator, "The authenticated user no longer exists.");

        // Resolve first: resolution saves any new project and may reset the change tracker on a race.
        var resolution = project is null
            ? null
            : await _projects.ResolveAsync(creatorId, ProjectIdentity.From(project.MachineId, project.Facts), cancellationToken);
        var projectId = resolution?.ProjectId;

        var skill = await _db.Skills
            .Include(s => s.CurrentVersion)
            .FirstOrDefaultAsync(s => s.CreatorId == creatorId && s.ProjectId == projectId && s.Name == name, cancellationToken);

        var now = DateTimeOffset.UtcNow;
        var isNewSkill = skill is null;
        skill ??= Skill.Create(creatorId, name, now, projectId);

        SkillVersionPushOutcome outcome;
        try
        {
            outcome = skill.PushVersion(creatorId, manifest, files, now, baseVersion);
        }
        catch (SkillVersionStaleException exception)
        {
            return SkillPushResult.Stale(name, exception.CurrentVersion, exception.Message, resolution);
        }
        catch (SkillCreatorViolationException)
        {
            return SkillPushResult.Failed(name, SkillPushError.NotCreator, "Only a skill's creator can push new versions of it.");
        }
        catch (SkillSizeExceededException exception)
        {
            return SkillPushResult.Failed(name, SkillPushError.TooLarge, exception.Message);
        }

        if (outcome.IsUnchanged)
            return SkillPushResult.Unchanged(name, outcome.Version.VersionNumber, skill.Id, outcome.Version.Id, files.Count, resolution);

        try
        {
            await PersistAsync(skill, outcome.Version, isNewSkill, cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return await LostRaceAsync(creatorId, projectId, name, outcome.Version, baseVersion, files.Count, resolution, cancellationToken);
        }

        return SkillPushResult.Created(name, outcome.Version.VersionNumber, skill.Id, outcome.Version.Id, files.Count, resolution);
    }

    /// <summary>The result for a push whose save lost a race with another push of the same skill.</summary>
    /// <remarks>
    /// Another push saved the same version number (or created the same new skill) between this push's read and
    /// its save, so this push's version was never stored. Reads the winner: identical content is the same edit
    /// arriving twice (a hook and a manual push, say), so it is unchanged; anything else is stale, exactly as
    /// if this push had arrived a moment later.
    /// </remarks>
    private async Task<SkillPushResult> LostRaceAsync(
        Guid creatorId, Guid? projectId, string name, SkillVersion lost, int? baseVersion, int fileCount, ProjectResolution? resolution,
        CancellationToken cancellationToken)
    {
        _db.ChangeTracker.Clear();
        var winner = await _db.Skills.AsNoTracking()
            .Where(s => s.CreatorId == creatorId && s.ProjectId == projectId && s.Name == name && s.CurrentVersion != null)
            .Select(s => new { SkillId = s.Id, s.CurrentVersion!.Id, s.CurrentVersion.VersionNumber, s.CurrentVersion.ContentHash })
            .FirstAsync(cancellationToken);

        if (winner.ContentHash == lost.ContentHash)
            return SkillPushResult.Unchanged(name, winner.VersionNumber, winner.SkillId, winner.Id, fileCount, resolution);

        var based = baseVersion ?? lost.VersionNumber - 1;
        return SkillPushResult.Stale(name, winner.VersionNumber, new SkillVersionStaleException(based, winner.VersionNumber).Message, resolution);
    }

    /// <summary>
    /// Persists a newly pushed version and dispatches its domain event.
    /// </summary>
    /// <remarks>
    /// A skill points at its current version (<c>Skills.CurrentVersionId</c>) while every version points
    /// back at its skill (<c>SkillVersions.SkillId</c>), so a brand-new skill and its first version form a
    /// foreign-key cycle EF Core cannot order into a single insert batch. For a new skill we insert the rows
    /// with the current-version pointer left null, then set it in a second save inside one transaction. The
    /// new version is added through <see cref="DbContext.Add(object)"/> (which also marks its files) because
    /// EF would otherwise read its domain-assigned key as an existing row and emit an UPDATE that stores
    /// nothing. An existing skill has no cycle, so its new version inserts and the pointer updates in a
    /// single save.
    /// </remarks>
    private async Task PersistAsync(
        Skill skill, SkillVersion newVersion, bool isNewSkill, CancellationToken cancellationToken)
    {
        if (!isNewSkill)
        {
            // Add (not just set state) so the version's files are marked Added too; the skill row already
            // exists, so EF inserts the version and updates the current-version pointer in one save.
            _db.Add(newVersion);
            await _db.SaveChangesRacingAndDispatchAsync(_dispatcher, cancellationToken);
            return;
        }

        var strategy = _db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);

            _db.Skills.Add(skill);
            var pointer = _db.Entry(skill).Property(s => s.CurrentVersionId);
            var currentVersionId = pointer.CurrentValue;
            pointer.CurrentValue = null;

            await _db.SaveChangesRacingAsync(cancellationToken);

            pointer.CurrentValue = currentVersionId;
            await _db.SaveChangesRacingAndDispatchAsync(_dispatcher, cancellationToken);

            await transaction.CommitAsync(cancellationToken);
        });
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
