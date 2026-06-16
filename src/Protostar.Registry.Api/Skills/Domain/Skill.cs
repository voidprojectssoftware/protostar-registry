using Protostar.Registry.Api.Common;
using Protostar.Registry.Api.Identity;

namespace Protostar.Registry.Api.Skills;

/// <summary>
/// The skill aggregate root: a creator plus a name, owning its version history. Content is never set from
/// outside; callers push a new version through <see cref="PushVersion"/>, which keeps the invariants
/// (creator-only writes, contiguous version numbers, current = newest, idempotent re-push) inside the
/// aggregate. A skill name is unique within a creator.
/// </summary>
public sealed class Skill : AggregateRoot
{
    private readonly List<SkillVersion> _versions = [];

    private Skill()
    {
    }

    public Guid Id { get; private set; }

    /// <summary>
    /// The user who first pushed this skill; the skill is namespaced to them. This is provenance, not an
    /// ownership or permissions grant (those come later).
    /// </summary>
    public Guid CreatorId { get; private set; }

    public User Creator { get; private set; } = default!;

    /// <summary>The skill name, from its SKILL.md front matter (or supplied at push). Unique per creator.</summary>
    public string Name { get; private set; } = default!;

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>The most recently pushed version; null only before the first version is pushed.</summary>
    public Guid? CurrentVersionId { get; private set; }

    public SkillVersion? CurrentVersion { get; private set; }

    /// <summary>Every version ever pushed, oldest first by <see cref="SkillVersion.VersionNumber"/>.</summary>
    public IReadOnlyCollection<SkillVersion> Versions => _versions;

    /// <summary>Starts a new skill owned by <paramref name="creatorId"/>. Its first version is added by a push.</summary>
    public static Skill Create(Guid creatorId, string name, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("A skill must have a name.", nameof(name));

        return new Skill
        {
            Id = Guid.NewGuid(),
            CreatorId = creatorId,
            Name = name,
            CreatedAt = now,
        };
    }

    /// <summary>
    /// Pushes a new version of this skill. Returns an idempotent no-op when the files match the current
    /// version byte-for-byte; otherwise appends a version, advances <see cref="CurrentVersion"/>, and
    /// raises <see cref="SkillVersionPushed"/>.
    /// </summary>
    /// <exception cref="SkillCreatorViolationException">
    /// <paramref name="pushedBy"/> is not this skill's creator.
    /// </exception>
    /// <exception cref="SkillSizeExceededException">
    /// <paramref name="files"/> exceed the limits in <see cref="SkillSizePolicy"/>.
    /// </exception>
    public SkillVersionPushOutcome PushVersion(
        Guid pushedBy, SkillManifest manifest, IReadOnlyList<SkillFileContent> files, DateTimeOffset now)
    {
        if (!SkillCreatorPolicy.CanPushVersion(this, pushedBy))
            throw new SkillCreatorViolationException(Id, pushedBy);

        if (SkillSizePolicy.Exceeded(files) is { } reason)
            throw new SkillSizeExceededException(reason);

        var contentHash = Sha256Hash.OfFiles(files);
        if (CurrentVersion is { } current && current.ContentHash == contentHash)
            return SkillVersionPushOutcome.Unchanged(current);

        // CurrentVersion is always the highest-numbered version, so next = current + 1 (or 1 when new).
        var versionNumber = (CurrentVersion?.VersionNumber ?? 0) + 1;
        var version = SkillVersion.Create(Id, versionNumber, manifest, files, contentHash, pushedBy, now);

        _versions.Add(version);
        CurrentVersion = version;
        CurrentVersionId = version.Id;

        RaiseDomainEvent(new SkillVersionPushed(Id, version.Id, versionNumber, pushedBy, now));
        return SkillVersionPushOutcome.Created(version);
    }
}
