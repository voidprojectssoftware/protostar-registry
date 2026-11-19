using Protostar.Registry.Api.Common;
using Protostar.Registry.Api.Identity;

namespace Protostar.Registry.Api.Skills;

/// <summary>
/// The skill aggregate root: a creator, an optional project and a name, owning its version history.
/// </summary>
/// <remarks>
/// Content is never set from outside; callers push a new version through <see cref="PushVersion"/>, which
/// keeps the invariants (creator-only writes, contiguous version numbers, current = newest, idempotent
/// re-push) inside the aggregate. A skill is identified by (creator, project, name), where no project means
/// a global skill (ADR 0002), so a global <c>deploy</c> and each project's <c>deploy</c> are separate skills
/// with separate histories.
/// </remarks>
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

    /// <summary>
    /// The project this skill belongs to, or <c>null</c> for a global skill (one authored in the harness's own
    /// config directory rather than inside a project).
    /// </summary>
    /// <remarks>
    /// A project skill and a global skill of the same name are different skills, and a global skill never gets a
    /// project. A project skill changes project only when the folder it belongs to is linked or unlinked
    /// (<see cref="FolderSkillMove"/>).
    /// </remarks>
    public Guid? ProjectId { get; private set; }

    /// <summary>
    /// The folder (<c>&lt;machine id&gt;:&lt;path&gt;</c>) whose link or unlink carried this skill into a project other
    /// folders share; <c>null</c> when it is where it was pushed, or in a project that is its folder's alone.
    /// </summary>
    /// <remarks>What lets an unlink take the folder's skills back out of a shared project, and only those.</remarks>
    public string? CarriedByFolderKey { get; private set; }

    /// <summary>The skill name, from its SKILL.md front matter (or supplied at push). Unique per creator and project.</summary>
    public string Name { get; private set; } = default!;

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>The most recently pushed version; null only before the first version is pushed.</summary>
    public Guid? CurrentVersionId { get; private set; }

    public SkillVersion? CurrentVersion { get; private set; }

    /// <summary>Every version ever pushed, oldest first by <see cref="SkillVersion.VersionNumber"/>.</summary>
    public IReadOnlyCollection<SkillVersion> Versions => _versions;

    /// <summary>True when <paramref name="name"/> can name a skill: not blank and free of <c>/</c> and <c>\</c>.</summary>
    /// <remarks>
    /// A skill is addressed by name in URLs (<c>/v1/skills/{name}/files</c>) and installed into a folder of
    /// that name, so a path separator in it would make the skill impossible to download or install.
    /// </remarks>
    public static bool IsValidName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && name.IndexOfAny(['/', '\\']) < 0;

    /// <summary>Starts a new skill owned by <paramref name="creatorId"/>. Its first version is added by a push.</summary>
    /// <param name="creatorId">The user pushing it.</param>
    /// <param name="name">The skill's name.</param>
    /// <param name="now">When it is created.</param>
    /// <param name="projectId">The project it belongs to; <c>null</c> for a global skill.</param>
    /// <exception cref="ArgumentException"><paramref name="name"/> fails <see cref="IsValidName"/>.</exception>
    public static Skill Create(Guid creatorId, string name, DateTimeOffset now, Guid? projectId = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("A skill must have a name.", nameof(name));
        if (!IsValidName(name))
            throw new ArgumentException("A skill name cannot contain '/' or '\\'.", nameof(name));

        return new Skill
        {
            Id = Guid.NewGuid(),
            CreatorId = creatorId,
            ProjectId = projectId,
            Name = name,
            CreatedAt = now,
        };
    }

    /// <summary>Moves this project skill, with its whole version history, to <paramref name="projectId"/> because its folder moved there.</summary>
    /// <param name="projectId">The project the folder now belongs to.</param>
    /// <param name="carriedByFolderKey">The folder that carried it, when the project is shared; <c>null</c> when the project is the folder's alone.</param>
    /// <exception cref="InvalidOperationException">This is a global skill, which belongs to no folder.</exception>
    public void MoveWithFolder(Guid projectId, string? carriedByFolderKey)
    {
        if (ProjectId is null)
            throw new InvalidOperationException("A global skill belongs to no folder, so it never moves with one.");
        ProjectId = projectId;
        CarriedByFolderKey = carriedByFolderKey;
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
