namespace Protostar.Registry.Api.Skills;

/// <summary>What the registry remembers of a skill name that was deleted or renamed away: its scope, last version and new name.</summary>
/// <remarks>
/// <para>
/// Without it a push cannot tell "the registry never had this skill" from "someone deliberately removed it", so a machine
/// that still has an unedited copy would put the skill back (registry#58). A push of that name whose base version is at
/// or below <see cref="LastVersion"/> came from a copy of the removed skill, and is refused (<see cref="Refuses"/>).
/// A push with base 0 (a copy that never synced a version) or no base (a client from before base versions) is not:
/// it creates a new skill of that name.
/// </para>
/// <para>
/// A tombstone exists only while its name has no live skill in its scope. When a new skill takes the name (pushed, or
/// renamed into it) the tombstone is dropped, and a new skill numbers its versions after <see cref="LastVersion"/>
/// (<see cref="Skill.Create"/>), so a copy of the removed skill can never pass for a copy of the new one: its base is
/// never the new skill's current version, and such a push is refused as stale.
/// </para>
/// </remarks>
public sealed class SkillTombstone
{
    private SkillTombstone()
    {
    }

    public Guid Id { get; private set; }

    /// <summary>The removed skill's creator, as on <see cref="Skill.CreatorId"/>.</summary>
    public Guid CreatorId { get; private set; }

    /// <summary>The removed skill's project; <c>null</c> for a global skill.</summary>
    public Guid? ProjectId { get; private set; }

    /// <summary>The name that was removed.</summary>
    public string Name { get; private set; } = default!;

    /// <summary>The removed skill's current version when it was removed under this name.</summary>
    public int LastVersion { get; private set; }

    /// <summary>The skill's name now, when it was renamed; <c>null</c> when it was deleted.</summary>
    /// <remarks>
    /// Follows the skill through later renames, and becomes <c>null</c> when the skill is deleted under its new name, so
    /// it always names a live skill or none.
    /// </remarks>
    public string? RenamedTo { get; private set; }

    /// <summary>When the name was removed.</summary>
    public DateTimeOffset RemovedAt { get; private set; }

    /// <summary>The tombstone for <paramref name="skill"/>, which is about to be deleted.</summary>
    /// <param name="skill">The skill being deleted.</param>
    /// <param name="lastVersion">Its current version number.</param>
    /// <param name="now">When it is deleted.</param>
    public static SkillTombstone Deleted(Skill skill, int lastVersion, DateTimeOffset now) => For(skill, skill.Name, lastVersion, renamedTo: null, now);

    /// <summary>The tombstone for <paramref name="oldName"/>, which <paramref name="skill"/> has just been renamed from.</summary>
    /// <param name="skill">The skill, already carrying its new name.</param>
    /// <param name="oldName">The name it had.</param>
    /// <param name="lastVersion">Its current version number.</param>
    /// <param name="now">When it was renamed.</param>
    /// <exception cref="ArgumentException"><paramref name="oldName"/> is the skill's name, so nothing was renamed.</exception>
    public static SkillTombstone Renamed(Skill skill, string oldName, int lastVersion, DateTimeOffset now)
    {
        if (oldName == skill.Name)
            throw new ArgumentException("A rename tombstone is for the name the skill no longer has.", nameof(oldName));
        return For(skill, oldName, lastVersion, skill.Name, now);
    }

    private static SkillTombstone For(Skill skill, string name, int lastVersion, string? renamedTo, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        CreatorId = skill.CreatorId,
        ProjectId = skill.ProjectId,
        Name = name,
        LastVersion = lastVersion,
        RenamedTo = renamedTo,
        RemovedAt = now,
    };

    /// <summary>True when a push of this name based on <paramref name="baseVersion"/> came from a copy of the removed skill.</summary>
    /// <param name="baseVersion">The version the pushed copy last synced; 0 when it never synced one, <c>null</c> when the pusher does not say.</param>
    public bool Refuses(int? baseVersion) => baseVersion is { } based && based >= 1 && based <= LastVersion;

    /// <summary>The skill this name was renamed to has been renamed again, to <paramref name="newName"/>.</summary>
    public void FollowRename(string newName) => RenamedTo = newName;

    /// <summary>The skill this name was renamed to has been deleted, so the name now reads as deleted.</summary>
    public void TargetDeleted() => RenamedTo = null;
}
