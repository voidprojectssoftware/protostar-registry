using Protostar.Registry.Api.Common;

namespace Protostar.Registry.Api.Skills;

/// <summary>
/// A new version of a skill was pushed. The seam the refinement loop and skill evaluators hang off:
/// they react to this rather than being wired into the push path.
/// </summary>
public sealed record SkillVersionPushed(
    Guid SkillId, Guid VersionId, int VersionNumber, Guid PushedById, DateTimeOffset PushedAt) : IDomainEvent;
