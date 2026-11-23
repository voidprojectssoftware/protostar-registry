using System.Text;
using Protostar.Registry.Api.Sessions;
using Protostar.Registry.Api.Skills;
using Xunit;

namespace Protostar.Registry.Tests.Sessions.Domain;

/// <summary>Contract tests for <see cref="SessionSkills.Match"/>: which registry skill and version a used name was.</summary>
public sealed class SessionSkillsTests
{
    private static readonly Guid Owner = Guid.NewGuid();
    private static readonly Guid Project = Guid.NewGuid();
    private static readonly DateTimeOffset Monday = new(2026, 11, 16, 9, 0, 0, TimeSpan.Zero);

    private static Skill SkillWithVersionsAt(string name, Guid? projectId, params DateTimeOffset[] pushedAt)
    {
        var skill = Skill.Create(Owner, name, pushedAt.FirstOrDefault(Monday), projectId);
        foreach (var at in pushedAt)
        {
            var file = SkillFileContent.Create(RelativePath.FromTrusted("SKILL.md"), Encoding.UTF8.GetBytes($"# {name} at {at:O}"));
            skill.PushVersion(Owner, SkillManifest.Empty, [file], at);
        }
        return skill;
    }

    private static SessionSkill MatchOne(NamedUse use, Guid? projectId, Skill[] skills, SkillTombstone[]? tombstones = null) =>
        Assert.Single(SessionSkills.Match([use], projectId, skills, tombstones ?? []));

    [Fact]
    public void The_version_is_the_newest_pushed_at_or_before_the_last_use()
    {
        var deploy = SkillWithVersionsAt("deploy", null, Monday, Monday.AddHours(2), Monday.AddHours(4));

        var used = MatchOne(new NamedUse("deploy", 2, Monday.AddHours(1), Monday.AddHours(2)), null, [deploy]);

        Assert.Equal(deploy.Id, used.SkillId);
        Assert.Equal(2, used.Version);
        Assert.False(used.Deleted);
    }

    [Fact]
    public void A_skill_edited_then_used_is_the_version_the_capture_hook_pushed_just_after_the_use()
    {
        // The hook runs on PostToolUse, so the edited content is pushed seconds after the Skill tool_use line.
        var use = Monday.AddHours(3);
        var deploy = SkillWithVersionsAt("deploy", null, Monday, use.AddSeconds(2));

        Assert.Equal(2, MatchOne(NamedUse.Once("deploy", use), null, [deploy]).Version);
    }

    [Fact]
    public void A_skill_first_used_then_pushed_by_the_capture_hook_is_version_1()
    {
        var use = Monday.AddHours(3);
        var deploy = SkillWithVersionsAt("deploy", null, use.AddSeconds(2));

        Assert.Equal(1, MatchOne(NamedUse.Once("deploy", use), null, [deploy]).Version);
    }

    [Fact]
    public void A_version_pushed_at_the_end_of_the_grace_counts()
    {
        var use = Monday.AddHours(3);
        var deploy = SkillWithVersionsAt("deploy", null, Monday, use + SessionSkills.PushGrace);

        Assert.Equal(2, MatchOne(NamedUse.Once("deploy", use), null, [deploy]).Version);
    }

    [Fact]
    public void A_later_edit_pushed_after_the_grace_keeps_the_version_in_force_at_the_use()
    {
        var use = Monday.AddHours(3);
        var deploy = SkillWithVersionsAt("deploy", null, Monday, use + SessionSkills.PushGrace + TimeSpan.FromSeconds(1));

        Assert.Equal(1, MatchOne(NamedUse.Once("deploy", use), null, [deploy]).Version);
    }

    [Fact]
    public void A_skill_first_pushed_after_the_grace_matches_with_no_version()
    {
        var use = Monday.AddHours(3);
        var deploy = SkillWithVersionsAt("deploy", null, use + SessionSkills.PushGrace + TimeSpan.FromSeconds(1));

        Assert.Null(MatchOne(NamedUse.Once("deploy", use), null, [deploy]).Version);
    }

    [Fact]
    public void A_skill_first_pushed_after_the_session_matches_with_no_version()
    {
        var deploy = SkillWithVersionsAt("deploy", null, Monday.AddDays(1));

        var used = MatchOne(NamedUse.Once("deploy", Monday), null, [deploy]);

        Assert.Equal(deploy.Id, used.SkillId);
        Assert.Null(used.Version);
    }

    [Fact]
    public void A_use_with_no_timestamp_matches_with_no_version()
    {
        var deploy = SkillWithVersionsAt("deploy", null, Monday);

        Assert.Null(MatchOne(NamedUse.Once("deploy", null), null, [deploy]).Version);
    }

    [Fact]
    public void A_plugin_skill_matches_nothing_even_when_a_skill_has_its_short_name()
    {
        var brainstorm = SkillWithVersionsAt("brainstorm", null, Monday);

        var used = MatchOne(NamedUse.Once("superpowers:brainstorm", Monday.AddHours(1)), null, [brainstorm]);

        Assert.Null(used.SkillId);
        Assert.False(used.Deleted);
    }

    [Fact]
    public void A_personal_skill_wins_over_a_project_skill_of_the_same_name()
    {
        var personal = SkillWithVersionsAt("deploy", null, Monday);
        var inProject = SkillWithVersionsAt("deploy", Project, Monday);

        Assert.Equal(personal.Id, MatchOne(NamedUse.Once("deploy", Monday.AddHours(1)), Project, [personal, inProject]).SkillId);
    }

    [Fact]
    public void A_project_skill_matches_when_there_is_no_personal_skill_of_that_name()
    {
        var inProject = SkillWithVersionsAt("deploy", Project, Monday);

        Assert.Equal(inProject.Id, MatchOne(NamedUse.Once("deploy", Monday.AddHours(1)), Project, [inProject]).SkillId);
    }

    [Fact]
    public void Another_projects_skill_never_matches()
    {
        var elsewhere = SkillWithVersionsAt("deploy", Guid.NewGuid(), Monday);

        Assert.Null(MatchOne(NamedUse.Once("deploy", Monday.AddHours(1)), Project, [elsewhere]).SkillId);
    }

    [Fact]
    public void A_deleted_skill_is_marked_deleted_with_no_skill_or_version()
    {
        var gone = SkillWithVersionsAt("deploy", null, Monday);
        var tombstone = SkillTombstone.Deleted(gone, 1, Monday.AddDays(1));

        var used = MatchOne(NamedUse.Once("deploy", Monday.AddHours(1)), null, [], [tombstone]);

        Assert.True(used.Deleted);
        Assert.Null(used.SkillId);
        Assert.Null(used.Version);
    }

    [Fact]
    public void A_skill_renamed_since_matches_under_its_new_name_with_its_versions()
    {
        var skill = SkillWithVersionsAt("deploy", null, Monday);
        skill.Rename(Owner, "ship", ["deploy"]);
        var tombstone = SkillTombstone.Renamed(skill, "deploy", 1, Monday.AddDays(1));

        var used = MatchOne(NamedUse.Once("deploy", Monday.AddHours(1)), null, [skill], [tombstone]);

        Assert.Equal(skill.Id, used.SkillId);
        Assert.Equal(1, used.Version);
        Assert.Equal("deploy", used.Name);
    }

    [Fact]
    public void A_deleted_personal_skill_is_reported_deleted_rather_than_falling_back_to_a_project_skill()
    {
        var inProject = SkillWithVersionsAt("deploy", Project, Monday);
        var tombstone = SkillTombstone.Deleted(SkillWithVersionsAt("deploy", null, Monday), 1, Monday.AddDays(1));

        Assert.True(MatchOne(NamedUse.Once("deploy", Monday.AddHours(1)), Project, [inProject], [tombstone]).Deleted);
    }

    [Fact]
    public void Results_are_earliest_first_used_first()
    {
        var matched = SessionSkills.Match(
            [NamedUse.Once("late", Monday.AddHours(2)), NamedUse.Once("untimed", null), NamedUse.Once("early", Monday)], null, [], []);

        Assert.Equal(["early", "late", "untimed"], matched.Select(m => m.Name));
    }
}
