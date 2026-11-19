namespace Protostar.Registry.Api.Skills;

/// <summary>
/// Raised when a user who is not a skill's creator attempts to change it (push, rename or delete). A guarded
/// invariant: the per-creator namespace means this should not occur through the normal paths, so it
/// signals a programming error rather than ordinary input the caller is expected to hit.
/// </summary>
public sealed class SkillCreatorViolationException(Guid skillId, Guid attemptedBy)
    : Exception($"User {attemptedBy} is not the creator of skill {skillId}, and only its creator can change it.")
{
    public Guid SkillId { get; } = skillId;

    public Guid AttemptedBy { get; } = attemptedBy;
}
