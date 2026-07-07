namespace Protostar.Registry.Api.Skills;

/// <summary>
/// Raised when a user who is not a skill's creator attempts to push a new version of it. A guarded
/// invariant: the per-creator namespace means this should not occur through the normal push path, so it
/// signals a programming error rather than ordinary input the caller is expected to hit.
/// </summary>
public sealed class SkillCreatorViolationException(Guid skillId, Guid attemptedBy)
    : Exception($"User {attemptedBy} is not the creator of skill {skillId} and cannot push versions to it.")
{
    public Guid SkillId { get; } = skillId;

    public Guid AttemptedBy { get; } = attemptedBy;
}
