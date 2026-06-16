using System.Text;
using Microsoft.EntityFrameworkCore;
using Protostar.Registry.Api.Common;
using Protostar.Registry.Api.Infrastructure;

namespace Protostar.Registry.Api.Skills;

/// <summary>
/// Application service for "push a local skill": it turns an upload into domain inputs (validated file
/// contents and a parsed manifest), loads or creates the <see cref="Skill"/> aggregate, and delegates the
/// version rules to <see cref="Skill.PushVersion"/>. The domain owns the invariants; this service owns the
/// transaction, the lookup, and translating outcomes into a transport-friendly <see cref="SkillPushResult"/>.
/// A push whose content matches the skill's current version is a no-op. Shared by the single and bulk
/// endpoints so neither re-implements validation, naming, or version bookkeeping.
/// </summary>
public sealed class SkillPushService
{
    private readonly RegistryDbContext _db;
    private readonly IDomainEventDispatcher _dispatcher;

    public SkillPushService(RegistryDbContext db, IDomainEventDispatcher dispatcher)
    {
        _db = db;
        _dispatcher = dispatcher;
    }

    /// <summary>
    /// Pushes one skill for <paramref name="creatorId"/> (the authenticated caller).
    /// <paramref name="fallbackName"/> names the skill when its SKILL.md omits a name (the bulk endpoint
    /// passes the skill's directory segment). Each call commits independently so one bad skill in a batch
    /// doesn't roll back the others.
    /// </summary>
    public async Task<SkillPushResult> PushAsync(
        Guid creatorId, string? fallbackName, IReadOnlyList<SkillFileUpload> uploads, CancellationToken cancellationToken)
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

        if (await _db.Users.FindAsync([creatorId], cancellationToken) is null)
            return SkillPushResult.Failed(name, SkillPushError.UnknownCreator, "The authenticated user no longer exists.");

        var skill = await _db.Skills
            .Include(s => s.CurrentVersion)
            .FirstOrDefaultAsync(s => s.CreatorId == creatorId && s.Name == name, cancellationToken);

        var now = DateTimeOffset.UtcNow;
        var isNewSkill = skill is null;
        skill ??= Skill.Create(creatorId, name, now);

        SkillVersionPushOutcome outcome;
        try
        {
            outcome = skill.PushVersion(creatorId, manifest, files, now);
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
            return SkillPushResult.Unchanged(name, outcome.Version.VersionNumber, skill.Id, outcome.Version.Id, files.Count);

        await PersistAsync(skill, outcome.Version, isNewSkill, cancellationToken);
        return SkillPushResult.Created(name, outcome.Version.VersionNumber, skill.Id, outcome.Version.Id, files.Count);
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
            await _db.SaveChangesAndDispatchAsync(_dispatcher, cancellationToken);
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

            await _db.SaveChangesAsync(cancellationToken);

            pointer.CurrentValue = currentVersionId;
            await _db.SaveChangesAndDispatchAsync(_dispatcher, cancellationToken);

            await transaction.CommitAsync(cancellationToken);
        });
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
