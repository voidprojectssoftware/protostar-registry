using Protostar.Registry.Api.Common;
using Protostar.Registry.Api.Identity;
using Protostar.Registry.Api.Projects;

namespace Protostar.Registry.Api.Skills;

/// <summary>
/// The skill aggregate root: a creator, an optional project and a name, owning its version history.
/// </summary>
/// <remarks>
/// Content is never set from outside; callers push a new version through <see cref="PushVersion"/>, which
/// keeps the invariants (creator-only writes, contiguous version numbers, current = newest, idempotent
/// re-push) inside the aggregate. Versions start at 1, or after the last version of a removed skill whose name this
/// one reuses (<see cref="SkillTombstone"/>). A skill is identified by (creator, project, name), where no project means
/// a global skill (ADR 0002), so a global <c>deploy</c> and each project's <c>deploy</c> are separate skills
/// with separate histories.
/// </remarks>
public sealed class Skill : AggregateRoot, IMovesWithFolder
{
    private readonly List<SkillVersion> _versions = [];

    // The version number the first push takes. Above 1 only for a new skill that reuses a removed skill's name; once a
    // version exists, numbering follows the current version, so this is never persisted.
    private int _firstVersionNumber = 1;

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
    /// (<see cref="FolderMove"/>).
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
    /// <param name="replacing">
    /// The tombstone of a removed skill whose name (in the same scope) this one takes; its versions are numbered after
    /// that skill's last version, so no copy of the removed skill can be mistaken for a copy of this one.
    /// </param>
    /// <exception cref="ArgumentException">
    /// <paramref name="name"/> fails <see cref="IsValidName"/>, or <paramref name="replacing"/> is another name's or scope's tombstone.
    /// </exception>
    public static Skill Create(Guid creatorId, string name, DateTimeOffset now, Guid? projectId = null, SkillTombstone? replacing = null)
    {
        if (replacing is not null && (replacing.CreatorId != creatorId || replacing.ProjectId != projectId || replacing.Name != name))
            throw new ArgumentException("A skill only replaces the tombstone of its own name and scope.", nameof(replacing));
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
            _firstVersionNumber = (replacing?.LastVersion ?? 0) + 1,
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

    /// <summary>Renames this skill, keeping its identity and whole version history.</summary>
    /// <param name="renamedBy">The user renaming it.</param>
    /// <param name="newName">The name it takes.</param>
    /// <param name="namesInScope">
    /// The names of every skill of the same creator and project (this one included), which the new name must not take.
    /// </param>
    /// <returns><c>false</c> when <paramref name="newName"/> is already this skill's name, so nothing changed.</returns>
    /// <remarks>
    /// The versions and their files hang off the skill row, so a rename touches only <see cref="Name"/>: nothing is
    /// copied, and the history reads the same under the new name. Names compare exactly, as the
    /// (creator, project, name) unique index does.
    /// </remarks>
    /// <exception cref="SkillCreatorViolationException"><paramref name="renamedBy"/> is not this skill's creator.</exception>
    /// <exception cref="ArgumentException"><paramref name="newName"/> fails <see cref="IsValidName"/>.</exception>
    /// <exception cref="SkillNameTakenException">Another skill in the same scope already has <paramref name="newName"/>.</exception>
    public bool Rename(Guid renamedBy, string newName, IEnumerable<string> namesInScope)
    {
        if (!SkillCreatorPolicy.CanChange(this, renamedBy))
            throw new SkillCreatorViolationException(Id, renamedBy);
        if (!IsValidName(newName))
            throw new ArgumentException("A skill name must not be blank or contain '/' or '\\'.", nameof(newName));
        if (newName == Name)
            return false;
        if (namesInScope.Contains(newName, StringComparer.Ordinal))
            throw new SkillNameTakenException(newName);

        Name = newName;
        return true;
    }

    /// <summary>Checks that <paramref name="deletedBy"/> may delete this skill, with every version and file it holds.</summary>
    /// <remarks>
    /// The caller removes the row; the versions and their files cascade from it. Deleting is the aggregate's call
    /// to allow because the same creator rule guards every change to a skill.
    /// </remarks>
    /// <exception cref="SkillCreatorViolationException"><paramref name="deletedBy"/> is not this skill's creator.</exception>
    public void EnsureCanDelete(Guid deletedBy)
    {
        if (!SkillCreatorPolicy.CanChange(this, deletedBy))
            throw new SkillCreatorViolationException(Id, deletedBy);
    }

    /// <summary>
    /// Pushes a new version of this skill. Returns an idempotent no-op when the files match the current
    /// version byte-for-byte; otherwise appends a version, advances <see cref="CurrentVersion"/>, and
    /// raises <see cref="SkillVersionPushed"/>.
    /// </summary>
    /// <param name="pushedBy">The user pushing.</param>
    /// <param name="manifest">The parsed SKILL.md.</param>
    /// <param name="files">Every file of the new version.</param>
    /// <param name="now">When it is pushed.</param>
    /// <param name="baseVersion">
    /// The version the pusher's copy was last synced from (0 when it never synced one), or <c>null</c> when the
    /// pusher does not say, as clients from before this rule do.
    /// </param>
    /// <remarks>
    /// A push that names a base must be based on the current version, or it would stack an edit made from an
    /// older version on top of a newer one and hide the newer one in history. The unchanged check comes first:
    /// content identical to the current version is never a conflict, whatever its base. A skill with no version
    /// yet (a new skill, or one deleted and pushed again) accepts any base, since there is nothing to overwrite.
    /// Two pushes racing from the same base both pass this check in memory; the unique (skill, version) index
    /// then lets only one of them be saved.
    /// </remarks>
    /// <exception cref="SkillCreatorViolationException">
    /// <paramref name="pushedBy"/> is not this skill's creator.
    /// </exception>
    /// <exception cref="SkillSizeExceededException">
    /// <paramref name="files"/> exceed the limits in <see cref="SkillSizePolicy"/>.
    /// </exception>
    /// <exception cref="SkillVersionStaleException">
    /// <paramref name="baseVersion"/> is given and is not the current version.
    /// </exception>
    public SkillVersionPushOutcome PushVersion(
        Guid pushedBy, SkillManifest manifest, IReadOnlyList<SkillFileContent> files, DateTimeOffset now, int? baseVersion = null)
    {
        if (!SkillCreatorPolicy.CanChange(this, pushedBy))
            throw new SkillCreatorViolationException(Id, pushedBy);

        if (SkillSizePolicy.Exceeded(files) is { } reason)
            throw new SkillSizeExceededException(reason);

        var contentHash = Sha256Hash.OfFiles(files);
        if (CurrentVersion is { } current && current.ContentHash == contentHash)
            return SkillVersionPushOutcome.Unchanged(current);

        if (baseVersion is { } based && CurrentVersion is { } latest && based != latest.VersionNumber)
            throw new SkillVersionStaleException(based, latest.VersionNumber);

        // CurrentVersion is always the highest-numbered version, so next = current + 1 (or the first number when new).
        var versionNumber = CurrentVersion is { } previous ? previous.VersionNumber + 1 : _firstVersionNumber;
        var version = SkillVersion.Create(Id, versionNumber, manifest, files, contentHash, pushedBy, now);

        _versions.Add(version);
        CurrentVersion = version;
        CurrentVersionId = version.Id;

        RaiseDomainEvent(new SkillVersionPushed(Id, version.Id, versionNumber, pushedBy, now));
        return SkillVersionPushOutcome.Created(version);
    }
}
