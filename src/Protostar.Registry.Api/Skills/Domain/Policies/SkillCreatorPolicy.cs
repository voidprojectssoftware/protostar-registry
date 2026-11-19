namespace Protostar.Registry.Api.Skills;

/// <summary>
/// The domain rule that only the user who created a skill may change it: push a version, rename it or delete it.
/// </summary>
/// <remarks>
/// Skills are namespaced per creator and adopting another user's skill forks it into the adopter's own namespace,
/// so a change never legitimately crosses creators. This policy states that invariant explicitly so the
/// aggregate can enforce it regardless of how a skill was loaded.
/// </remarks>
public static class SkillCreatorPolicy
{
    /// <summary>Whether <paramref name="userId"/> may change <paramref name="skill"/>.</summary>
    public static bool CanChange(Skill skill, Guid userId) => skill.CreatorId == userId;
}
