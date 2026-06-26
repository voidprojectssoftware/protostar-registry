using System.Text;
using Protostar.Registry.Api.Skills;

namespace Protostar.Registry.Tests.Skills.Domain;

public sealed class SkillTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    private static Skill NewSkill(out Guid creator, string name = "demo")
    {
        creator = Guid.NewGuid();
        return Skill.Create(creator, name, Now);
    }

    private static SkillFileContent FileWith(string text) =>
        SkillFileContent.Create(RelativePath.FromTrusted("SKILL.md"), Encoding.UTF8.GetBytes(text));

    private static SkillVersionPushOutcome Push(Skill skill, Guid pusher, string fileText) =>
        skill.PushVersion(pusher, SkillManifest.Empty, new[] { FileWith(fileText) }, Now);

    // ----- Create -----

    [Fact]
    public void Create_sets_the_creator_to_the_given_creator_id()
    {
        var skill = NewSkill(out var creator);

        Assert.Equal(creator, skill.CreatorId);
    }

    [Fact]
    public void Create_sets_the_name_to_the_given_name()
    {
        var creator = Guid.NewGuid();

        var skill = Skill.Create(creator, "my-skill", Now);

        Assert.Equal("my-skill", skill.Name);
    }

    [Fact]
    public void Create_leaves_current_version_null_before_any_push()
    {
        var skill = NewSkill(out _);

        Assert.Null(skill.CurrentVersion);
    }

    [Fact]
    public void Create_leaves_current_version_id_null_before_any_push()
    {
        var skill = NewSkill(out _);

        Assert.Null(skill.CurrentVersionId);
    }

    [Fact]
    public void Create_leaves_versions_empty_before_any_push()
    {
        var skill = NewSkill(out _);

        Assert.Empty(skill.Versions);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    [InlineData("   ")]
    public void Create_throws_when_the_name_is_blank_or_whitespace(string name)
    {
        var creator = Guid.NewGuid();

        Assert.Throws<ArgumentException>(() => Skill.Create(creator, name, Now));
    }

    [Fact]
    public void Create_throws_when_the_name_is_null()
    {
        var creator = Guid.NewGuid();

        Assert.Throws<ArgumentException>(() => Skill.Create(creator, null!, Now));
    }

    // ----- First push -----

    [Fact]
    public void PushVersion_first_push_returns_an_outcome_that_is_not_unchanged()
    {
        var skill = NewSkill(out var creator);

        var outcome = Push(skill, creator, "v1");

        Assert.False(outcome.IsUnchanged);
    }

    [Fact]
    public void PushVersion_first_push_numbers_the_version_one()
    {
        var skill = NewSkill(out var creator);

        var outcome = Push(skill, creator, "v1");

        Assert.Equal(1, outcome.Version.VersionNumber);
    }

    [Fact]
    public void PushVersion_first_push_sets_current_version()
    {
        var skill = NewSkill(out var creator);

        Push(skill, creator, "v1");

        Assert.NotNull(skill.CurrentVersion);
    }

    [Fact]
    public void PushVersion_first_push_makes_current_version_the_returned_version()
    {
        var skill = NewSkill(out var creator);

        var outcome = Push(skill, creator, "v1");

        Assert.Equal(outcome.Version.Id, skill.CurrentVersion!.Id);
    }

    [Fact]
    public void PushVersion_first_push_sets_current_version_id_to_the_returned_version_id()
    {
        var skill = NewSkill(out var creator);

        var outcome = Push(skill, creator, "v1");

        Assert.Equal(outcome.Version.Id, skill.CurrentVersionId);
    }

    [Fact]
    public void PushVersion_first_push_grows_versions_to_one()
    {
        var skill = NewSkill(out var creator);

        Push(skill, creator, "v1");

        Assert.Single(skill.Versions);
    }

    [Fact]
    public void PushVersion_first_push_records_pushed_by_as_the_creator()
    {
        var skill = NewSkill(out var creator);

        var outcome = Push(skill, creator, "v1");

        Assert.Equal(creator, outcome.Version.PushedById);
    }

    // ----- First push: domain event -----

    [Fact]
    public void PushVersion_first_push_raises_a_skill_version_pushed_event()
    {
        var skill = NewSkill(out var creator);

        Push(skill, creator, "v1");

        Assert.Single(skill.DomainEvents);
    }

    [Fact]
    public void PushVersion_first_push_event_carries_version_number_one()
    {
        var skill = NewSkill(out var creator);

        Push(skill, creator, "v1");

        var pushed = Assert.IsType<SkillVersionPushed>(Assert.Single(skill.DomainEvents));
        Assert.Equal(1, pushed.VersionNumber);
    }

    [Fact]
    public void PushVersion_first_push_event_carries_the_skill_id()
    {
        var skill = NewSkill(out var creator);

        Push(skill, creator, "v1");

        var pushed = Assert.IsType<SkillVersionPushed>(Assert.Single(skill.DomainEvents));
        Assert.Equal(skill.Id, pushed.SkillId);
    }

    [Fact]
    public void PushVersion_first_push_event_carries_the_pusher_id()
    {
        var skill = NewSkill(out var creator);

        Push(skill, creator, "v1");

        var pushed = Assert.IsType<SkillVersionPushed>(Assert.Single(skill.DomainEvents));
        Assert.Equal(creator, pushed.PushedById);
    }

    [Fact]
    public void PushVersion_first_push_event_carries_the_returned_version_id()
    {
        var skill = NewSkill(out var creator);

        var outcome = Push(skill, creator, "v1");

        var pushed = Assert.IsType<SkillVersionPushed>(Assert.Single(skill.DomainEvents));
        Assert.Equal(outcome.Version.Id, pushed.VersionId);
    }

    // ----- Second push with different content -----

    [Fact]
    public void PushVersion_second_push_with_different_content_numbers_the_version_two()
    {
        var skill = NewSkill(out var creator);
        Push(skill, creator, "v1");

        var outcome = Push(skill, creator, "v2");

        Assert.Equal(2, outcome.Version.VersionNumber);
    }

    [Fact]
    public void PushVersion_second_push_with_different_content_is_not_unchanged()
    {
        var skill = NewSkill(out var creator);
        Push(skill, creator, "v1");

        var outcome = Push(skill, creator, "v2");

        Assert.False(outcome.IsUnchanged);
    }

    [Fact]
    public void PushVersion_second_push_with_different_content_advances_current_version()
    {
        var skill = NewSkill(out var creator);
        Push(skill, creator, "v1");

        var outcome = Push(skill, creator, "v2");

        Assert.Equal(outcome.Version.Id, skill.CurrentVersion!.Id);
    }

    [Fact]
    public void PushVersion_second_push_with_different_content_grows_versions_to_two()
    {
        var skill = NewSkill(out var creator);
        Push(skill, creator, "v1");

        Push(skill, creator, "v2");

        Assert.Equal(2, skill.Versions.Count);
    }

    // ----- Idempotent re-push (same content as current) -----

    [Fact]
    public void PushVersion_repush_of_identical_content_is_unchanged()
    {
        var skill = NewSkill(out var creator);
        Push(skill, creator, "v1");

        var outcome = Push(skill, creator, "v1");

        Assert.True(outcome.IsUnchanged);
    }

    [Fact]
    public void PushVersion_repush_of_identical_content_returns_the_current_version()
    {
        var skill = NewSkill(out var creator);
        Push(skill, creator, "v1");

        var outcome = Push(skill, creator, "v1");

        Assert.Equal(skill.CurrentVersion!.Id, outcome.Version.Id);
    }

    [Fact]
    public void PushVersion_repush_of_identical_content_does_not_grow_versions()
    {
        var skill = NewSkill(out var creator);
        Push(skill, creator, "v1");

        Push(skill, creator, "v1");

        Assert.Single(skill.Versions);
    }

    [Fact]
    public void PushVersion_repush_of_identical_content_raises_no_additional_event()
    {
        var skill = NewSkill(out var creator);
        Push(skill, creator, "v1");

        // Clear the event from the first (real) push so we isolate the re-push.
        skill.ClearDomainEvents();
        Push(skill, creator, "v1");

        Assert.Empty(skill.DomainEvents);
    }

    // ----- Creator enforcement -----

    [Fact]
    public void PushVersion_throws_when_the_pusher_is_not_the_creator()
    {
        var skill = NewSkill(out _);
        var stranger = Guid.NewGuid();

        Assert.Throws<SkillCreatorViolationException>(() => Push(skill, stranger, "v1"));
    }

    [Fact]
    public void PushVersion_by_a_non_creator_leaves_versions_empty()
    {
        // ASSUMES: the creator check happens before any state change, so a rejected push is a no-op.
        var skill = NewSkill(out _);
        var stranger = Guid.NewGuid();

        Assert.Throws<SkillCreatorViolationException>(() => Push(skill, stranger, "v1"));
        Assert.Empty(skill.Versions);
    }

    // ----- Size enforcement -----

    [Fact]
    public void PushVersion_throws_when_a_file_exceeds_the_max_file_size()
    {
        var skill = NewSkill(out var creator);
        var oversized = SkillFileContent.Create(
            RelativePath.FromTrusted("SKILL.md"),
            new byte[SkillSizePolicy.MaxFileBytes + 1]);

        Assert.Throws<SkillSizeExceededException>(
            () => skill.PushVersion(creator, SkillManifest.Empty, new[] { oversized }, Now));
    }

    [Fact]
    public void PushVersion_with_an_oversized_file_leaves_versions_empty()
    {
        // ASSUMES: the size check happens before any state change, so a rejected push is a no-op.
        var skill = NewSkill(out var creator);
        var oversized = SkillFileContent.Create(
            RelativePath.FromTrusted("SKILL.md"),
            new byte[SkillSizePolicy.MaxFileBytes + 1]);

        Assert.Throws<SkillSizeExceededException>(
            () => skill.PushVersion(creator, SkillManifest.Empty, new[] { oversized }, Now));
        Assert.Empty(skill.Versions);
    }

    // ----- ClearDomainEvents -----

    [Fact]
    public void ClearDomainEvents_empties_the_domain_events()
    {
        var skill = NewSkill(out var creator);
        Push(skill, creator, "v1");

        skill.ClearDomainEvents();

        Assert.Empty(skill.DomainEvents);
    }
}
