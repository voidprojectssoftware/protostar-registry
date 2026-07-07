namespace Protostar.Registry.Api.Skills;

/// <summary>
/// The domain rule that a skill's versions may only be pushed by the user who created it. Skills are
/// namespaced per creator and adopting another user's skill forks it into the adopter's own namespace,
/// so a push never legitimately crosses creators. This policy states that invariant explicitly so the
/// aggregate can enforce it regardless of how a skill was loaded.
/// </summary>
public static class SkillCreatorPolicy
{
    /// <summary>Whether <paramref name="creatorId"/> may push a new version of <paramref name="skill"/>.</summary>
    public static bool CanPushVersion(Skill skill, Guid creatorId) => skill.CreatorId == creatorId;
}
