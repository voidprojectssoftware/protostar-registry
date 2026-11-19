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
    public void Create_without_a_project_makes_a_global_skill()
    {
        var skill = NewSkill(out _);

        Assert.Null(skill.ProjectId);
    }

    [Fact]
    public void Create_with_a_project_makes_a_skill_of_that_project()
    {
        var project = Guid.NewGuid();

        var skill = Skill.Create(Guid.NewGuid(), "deploy", Now, project);

        Assert.Equal(project, skill.ProjectId);
    }

    [Fact]
    public void A_global_skill_and_a_project_skill_of_the_same_name_keep_separate_histories()
    {
        var creator = Guid.NewGuid();
        var global = Skill.Create(creator, "deploy", Now);
        var project = Skill.Create(creator, "deploy", Now, Guid.NewGuid());

        Push(global, creator, "global v1");
        Push(global, creator, "global v2");
        var projectFirst = Push(project, creator, "project v1");

        Assert.Equal(2, global.CurrentVersion!.VersionNumber);
        Assert.Equal(1, projectFirst.Version.VersionNumber);
        Assert.NotEqual(global.Id, project.Id);
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

    [Theory]
    [InlineData("team/skill")]
    [InlineData("a\\b")]
    [InlineData("/lead")]
    [InlineData("trail/")]
    public void Create_throws_when_the_name_contains_a_path_separator(string name)
    {
        // A name is a URL segment and a folder name, so a separator would make the skill unreachable.
        var creator = Guid.NewGuid();

        Assert.Throws<ArgumentException>(() => Skill.Create(creator, name, Now));
    }

    [Theory]
    [InlineData("team/skill", false)]
    [InlineData("a\\b", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("my-skill", true)]
    [InlineData("my skill.v2", true)]
    public void IsValidName_refuses_blank_names_and_path_separators(string? name, bool expected)
    {
        Assert.Equal(expected, Skill.IsValidName(name));
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

    // ----- Rename -----

    [Fact]
    public void Rename_changes_the_name()
    {
        var skill = NewSkill(out var creator, "notes");

        var changed = skill.Rename(creator, "journal", ["notes"]);

        Assert.True(changed);
        Assert.Equal("journal", skill.Name);
    }

    [Fact]
    public void Rename_keeps_the_id_project_and_every_version()
    {
        var project = Guid.NewGuid();
        var creator = Guid.NewGuid();
        var skill = Skill.Create(creator, "notes", Now, project);
        Push(skill, creator, "v1");
        Push(skill, creator, "v2");
        var id = skill.Id;
        var versions = skill.Versions.Select(v => v.Id).ToList();

        skill.Rename(creator, "journal", ["notes"]);

        Assert.Equal(id, skill.Id);
        Assert.Equal(project, skill.ProjectId);
        Assert.Equal(versions, skill.Versions.Select(v => v.Id));
        Assert.Equal(2, skill.CurrentVersion!.VersionNumber);
    }

    [Fact]
    public void Rename_to_the_current_name_changes_nothing_and_says_so()
    {
        var skill = NewSkill(out var creator, "notes");

        var changed = skill.Rename(creator, "notes", ["notes"]);

        Assert.False(changed);
        Assert.Equal("notes", skill.Name);
    }

    [Fact]
    public void Rename_to_a_name_taken_in_the_scope_throws_and_leaves_the_name()
    {
        var skill = NewSkill(out var creator, "notes");

        var ex = Assert.Throws<SkillNameTakenException>(() => skill.Rename(creator, "journal", ["notes", "journal"]));

        Assert.Equal("journal", ex.Name);
        Assert.Equal("notes", skill.Name);
    }

    [Fact]
    public void Rename_compares_names_exactly_so_a_different_case_is_not_taken()
    {
        // ASSUMES: names compare as the (creator, project, name) unique index does, which is case-sensitive.
        var skill = NewSkill(out var creator, "notes");

        skill.Rename(creator, "Journal", ["notes", "journal"]);

        Assert.Equal("Journal", skill.Name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    public void Rename_to_an_invalid_name_throws_and_leaves_the_name(string newName)
    {
        var skill = NewSkill(out var creator, "notes");

        Assert.Throws<ArgumentException>(() => skill.Rename(creator, newName, ["notes"]));
        Assert.Equal("notes", skill.Name);
    }

    [Fact]
    public void Rename_by_someone_other_than_the_creator_throws()
    {
        var skill = NewSkill(out _, "notes");

        Assert.Throws<SkillCreatorViolationException>(() => skill.Rename(Guid.NewGuid(), "journal", ["notes"]));
        Assert.Equal("notes", skill.Name);
    }

    // ----- EnsureCanDelete -----

    [Fact]
    public void EnsureCanDelete_allows_the_creator()
    {
        var skill = NewSkill(out var creator);

        skill.EnsureCanDelete(creator);
    }

    [Fact]
    public void EnsureCanDelete_refuses_anyone_else()
    {
        var skill = NewSkill(out _);

        Assert.Throws<SkillCreatorViolationException>(() => skill.EnsureCanDelete(Guid.NewGuid()));
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
