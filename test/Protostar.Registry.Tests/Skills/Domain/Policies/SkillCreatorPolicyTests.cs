using System;
using Protostar.Registry.Api.Skills;

namespace Protostar.Registry.Tests.Skills.Domain.Policies;

/// <summary>
/// Black-box contract tests for <see cref="SkillCreatorPolicy"/>. Derived solely from the documented
/// invariant: a skill's versions may only be pushed by the user who created it.
/// </summary>
public sealed class SkillCreatorPolicyTests
{
    private static Skill NewSkillOwnedBy(Guid creatorId) =>
        Skill.Create(creatorId, "my-skill", DateTimeOffset.UtcNow);

    [Fact]
    public void CanPushVersion_is_true_for_the_skills_creator()
    {
        var creatorId = Guid.NewGuid();
        var skill = NewSkillOwnedBy(creatorId);

        var result = SkillCreatorPolicy.CanPushVersion(skill, creatorId);

        Assert.True(result);
    }

    [Fact]
    public void CanPushVersion_is_false_for_a_different_user()
    {
        var creatorId = Guid.NewGuid();
        var otherUserId = Guid.NewGuid();
        var skill = NewSkillOwnedBy(creatorId);

        var result = SkillCreatorPolicy.CanPushVersion(skill, otherUserId);

        Assert.False(result);
    }

    [Fact]
    public void CanPushVersion_is_false_for_the_empty_guid_against_a_real_creator()
    {
        // The empty/anonymous id is not the creator, so it cannot push. (The rule is creator-id
        // equality; a skill created by a real user is never pushable by Guid.Empty.)
        var skill = NewSkillOwnedBy(Guid.NewGuid());

        var result = SkillCreatorPolicy.CanPushVersion(skill, Guid.Empty);

        Assert.False(result);
    }
}
