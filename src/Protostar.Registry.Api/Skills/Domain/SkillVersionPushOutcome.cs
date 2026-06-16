namespace Protostar.Registry.Api.Skills;

/// <summary>
/// The result of <see cref="Skill.PushVersion"/>: either a newly stored version, or the existing current
/// version when the push was byte-for-byte identical to it (an idempotent no-op).
/// </summary>
public sealed class SkillVersionPushOutcome
{
    private SkillVersionPushOutcome(SkillVersion version, bool isUnchanged)
    {
        Version = version;
        IsUnchanged = isUnchanged;
    }

    public SkillVersion Version { get; }

    /// <summary>True when the push matched the current version, so no new version was created.</summary>
    public bool IsUnchanged { get; }

    public static SkillVersionPushOutcome Created(SkillVersion version) => new(version, isUnchanged: false);

    public static SkillVersionPushOutcome Unchanged(SkillVersion version) => new(version, isUnchanged: true);
}
