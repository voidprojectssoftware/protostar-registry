using System.Text;
using Protostar.Registry.Api.Skills;

namespace Protostar.Registry.Tests.Skills.Domain;

public sealed class SkillTombstoneTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    private static SkillVersionPushOutcome Push(Skill skill, string text, int? baseVersion = null) =>
        skill.PushVersion(skill.CreatorId, SkillManifest.Empty,
            new[] { SkillFileContent.Create(RelativePath.FromTrusted("SKILL.md"), Encoding.UTF8.GetBytes(text)) }, Now, baseVersion);

    private static Skill SkillAt(int versions, string name = "tidy", Guid? projectId = null)
    {
        var skill = Skill.Create(Guid.NewGuid(), name, Now, projectId);
        for (var v = 1; v <= versions; v++)
            Push(skill, $"v{v}");
        return skill;
    }

    // ----- Deleted / Renamed -----

    [Fact]
    public void Deleted_remembers_the_name_scope_and_last_version_and_names_no_new_name()
    {
        var project = Guid.NewGuid();
        var skill = SkillAt(3, projectId: project);

        var tombstone = SkillTombstone.Deleted(skill, 3, Now);

        Assert.Equal(("tidy", project, skill.CreatorId, 3), (tombstone.Name, tombstone.ProjectId, tombstone.CreatorId, tombstone.LastVersion));
        Assert.Null(tombstone.RenamedTo);
        Assert.Equal(Now, tombstone.RemovedAt);
    }

    [Fact]
    public void Renamed_remembers_the_old_name_and_names_the_new_one()
    {
        var skill = SkillAt(2, "greet-user");
        skill.Rename(skill.CreatorId, "hello", ["greet-user"]);

        var tombstone = SkillTombstone.Renamed(skill, "greet-user", 2, Now);

        Assert.Equal(("greet-user", "hello", 2), (tombstone.Name, tombstone.RenamedTo, tombstone.LastVersion));
    }

    [Fact]
    public void Renamed_refuses_the_name_the_skill_still_has()
    {
        var skill = SkillAt(1, "greet-user");

        Assert.Throws<ArgumentException>(() => SkillTombstone.Renamed(skill, "greet-user", 1, Now));
    }

    [Fact]
    public void Remembers_the_content_hash_of_each_version_up_to_the_last_oldest_first()
    {
        var skill = SkillAt(2, "greet-user");
        skill.Rename(skill.CreatorId, "hello", ["greet-user"]);

        var tombstone = SkillTombstone.Renamed(skill, "greet-user", 2, Now,
            [new RemovedSkillVersion(3, "C"), new RemovedSkillVersion(2, "B"), new RemovedSkillVersion(1, "A")]);

        Assert.Equal([new RemovedSkillVersion(1, "A"), new RemovedSkillVersion(2, "B")], tombstone.Versions);
    }

    [Fact]
    public void A_tombstone_given_no_versions_remembers_no_hashes()
    {
        var tombstone = SkillTombstone.Deleted(SkillAt(3), 3, Now);

        Assert.Empty(tombstone.Versions);
    }

    // ----- Refuses -----

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Refuses_a_push_based_on_any_version_of_the_removed_skill(int baseVersion)
    {
        var tombstone = SkillTombstone.Deleted(SkillAt(3), 3, Now);

        Assert.True(tombstone.Refuses(baseVersion));
    }

    [Fact]
    public void Accepts_a_push_from_a_copy_that_never_synced_a_version()
    {
        var tombstone = SkillTombstone.Deleted(SkillAt(3), 3, Now);

        Assert.False(tombstone.Refuses(0));
    }

    [Fact]
    public void Accepts_a_push_that_names_no_base_as_older_clients_send()
    {
        var tombstone = SkillTombstone.Deleted(SkillAt(3), 3, Now);

        Assert.False(tombstone.Refuses(null));
    }

    [Fact]
    public void Accepts_a_push_based_on_a_version_after_the_removed_skill()
    {
        var tombstone = SkillTombstone.Deleted(SkillAt(3), 3, Now);

        Assert.False(tombstone.Refuses(4));
    }

    // ----- following the skill -----

    [Fact]
    public void FollowRename_points_the_tombstone_at_the_skill_s_newest_name()
    {
        var skill = SkillAt(1, "greet-user");
        skill.Rename(skill.CreatorId, "hello", ["greet-user"]);
        var tombstone = SkillTombstone.Renamed(skill, "greet-user", 1, Now);

        tombstone.FollowRename("hi");

        Assert.Equal("hi", tombstone.RenamedTo);
    }

    [Fact]
    public void TargetDeleted_turns_a_rename_into_a_delete_and_still_refuses_old_copies()
    {
        var skill = SkillAt(1, "greet-user");
        skill.Rename(skill.CreatorId, "hello", ["greet-user"]);
        var tombstone = SkillTombstone.Renamed(skill, "greet-user", 1, Now);

        tombstone.TargetDeleted();

        Assert.Null(tombstone.RenamedTo);
        Assert.True(tombstone.Refuses(1));
    }

    // ----- a new skill that reuses the name -----

    [Fact]
    public void A_new_skill_replacing_a_tombstone_numbers_its_versions_after_the_removed_skill()
    {
        var old = SkillAt(3);
        var tombstone = SkillTombstone.Deleted(old, 3, Now);

        var reused = Skill.Create(old.CreatorId, "tidy", Now, replacing: tombstone);
        var first = Push(reused, "a brand-new tidy", baseVersion: 0);
        var second = Push(reused, "edited", baseVersion: 4);

        Assert.Equal(4, first.Version.VersionNumber);
        Assert.Equal(5, second.Version.VersionNumber);
    }

    [Fact]
    public void A_copy_of_the_removed_skill_is_stale_against_the_new_skill_of_that_name()
    {
        var old = SkillAt(1);
        var reused = Skill.Create(old.CreatorId, "tidy", Now, replacing: SkillTombstone.Deleted(old, 1, Now));
        Push(reused, "a brand-new tidy", baseVersion: 0);

        var stale = Assert.Throws<SkillVersionStaleException>(() => Push(reused, "old tidy, edited", baseVersion: 1));

        Assert.Equal(2, stale.CurrentVersion);
    }

    [Fact]
    public void A_new_skill_without_a_tombstone_starts_at_version_1()
    {
        var skill = Skill.Create(Guid.NewGuid(), "tidy", Now);

        Assert.Equal(1, Push(skill, "v1").Version.VersionNumber);
    }

    [Theory]
    [InlineData("other", false, false)]
    [InlineData("tidy", true, false)]
    [InlineData("tidy", false, true)]
    public void A_new_skill_refuses_another_name_s_or_scope_s_tombstone(string name, bool otherCreator, bool otherProject)
    {
        var old = SkillAt(1);
        var tombstone = SkillTombstone.Deleted(old, 1, Now);

        Assert.Throws<ArgumentException>(() => Skill.Create(
            otherCreator ? Guid.NewGuid() : old.CreatorId, name, Now, otherProject ? Guid.NewGuid() : null, tombstone));
    }
}
