using System.Text;
using Protostar.Registry.Api.Agents;
using Protostar.Registry.Api.Sessions;
using Protostar.Registry.Api.Skills;
using Xunit;

namespace Protostar.Registry.Tests.Sessions.Domain;

/// <summary>Contract tests for <see cref="SessionAgents.Match"/>: which agent types were custom agents, and which version each was.</summary>
public sealed class SessionAgentsTests
{
    private static readonly Guid Owner = Guid.NewGuid();
    private static readonly Guid Project = Guid.NewGuid();
    private static readonly DateTimeOffset Monday = new(2026, 11, 16, 9, 0, 0, TimeSpan.Zero);

    private static Agent AgentWithVersionsAt(string name, Guid? projectId, params DateTimeOffset[] pushedAt)
    {
        var agent = Agent.Create(Owner, name, pushedAt.FirstOrDefault(Monday), projectId);
        foreach (var at in pushedAt)
        {
            var manifest = new AgentManifest { Name = name, Description = "Test agent." };
            agent.PushVersion(Owner, manifest, RelativePath.FromTrusted($"{name}.md"), Encoding.UTF8.GetBytes($"---\nname: {name}\n---\nat {at:O}"), at);
        }
        return agent;
    }

    private static IReadOnlyList<SessionAgent> Match(
        IReadOnlyList<NamedUse> uses, Guid? projectId, DateTimeOffset? endedAt, Agent[] agents, AgentTombstone[]? tombstones = null) =>
        SessionAgents.Match(
            uses, projectId, endedAt, agents,
            [.. agents.SelectMany(a => a.Versions.Select(v => new AgentVersionStamp(a.Id, v.VersionNumber, v.CreatedAt)))],
            tombstones ?? []);

    [Fact]
    public void Only_the_owners_agents_are_listed_so_built_in_types_are_dropped()
    {
        var reviewer = AgentWithVersionsAt("reviewer", null, Monday);
        NamedUse[] uses = [NamedUse.Once("general-purpose", Monday), NamedUse.Once("Explore", Monday), NamedUse.Once("reviewer", Monday), NamedUse.Once("Plan", Monday)];

        var used = Assert.Single(Match(uses, null, Monday.AddHours(1), [reviewer]));

        Assert.Equal("reviewer", used.Name);
        Assert.Equal(reviewer.Id, used.AgentId);
        Assert.Null(used.ProjectId);
        Assert.Equal(1, used.Version);
    }

    [Fact]
    public void A_plugin_agent_never_matches_even_when_an_agent_has_that_name()
    {
        // Agent names may hold a colon, but plugins' agents are never pushed, so the plugin form is never one of them.
        var sameName = AgentWithVersionsAt("superpowers:reviewer", null, Monday);

        Assert.Empty(Match([NamedUse.Once("superpowers:reviewer", Monday)], null, Monday.AddHours(1), [sameName]));
    }

    [Fact]
    public void A_project_agent_wins_over_a_personal_agent_of_the_same_name()
    {
        var personal = AgentWithVersionsAt("reviewer", null, Monday);
        var inProject = AgentWithVersionsAt("reviewer", Project, Monday, Monday.AddMinutes(5));

        var used = Assert.Single(Match([NamedUse.Once("reviewer", Monday.AddHours(1))], Project, Monday.AddHours(1), [personal, inProject]));

        Assert.Equal(inProject.Id, used.AgentId);
        Assert.Equal(Project, used.ProjectId);
        Assert.Equal(2, used.Version);
    }

    [Fact]
    public void A_session_in_no_project_matches_the_personal_agent()
    {
        var personal = AgentWithVersionsAt("reviewer", null, Monday);
        var inProject = AgentWithVersionsAt("reviewer", Project, Monday);

        Assert.Equal(personal.Id, Assert.Single(Match([NamedUse.Once("reviewer", Monday)], null, Monday, [personal, inProject])).AgentId);
    }

    [Fact]
    public void A_name_the_project_does_not_have_falls_back_to_the_personal_agent()
    {
        var personal = AgentWithVersionsAt("reviewer", null, Monday);

        Assert.Equal(personal.Id, Assert.Single(Match([NamedUse.Once("reviewer", Monday)], Project, Monday, [personal])).AgentId);
    }

    [Fact]
    public void The_version_is_the_newest_pushed_by_the_end_of_the_session_allowing_the_grace()
    {
        // Created and used in one session: the SessionEnd hook pushes it after the session's last line.
        var end = Monday.AddHours(3);
        var reviewer = AgentWithVersionsAt("reviewer", null, end + SessionAgents.PushGrace - TimeSpan.FromSeconds(1), end.AddHours(1));

        var used = Assert.Single(Match([NamedUse.Once("reviewer", Monday.AddHours(1))], null, end, [reviewer]));

        Assert.Equal(1, used.Version);
    }

    [Fact]
    public void An_agent_first_pushed_after_the_session_has_no_version()
    {
        var reviewer = AgentWithVersionsAt("reviewer", null, Monday.AddDays(1));

        var used = Assert.Single(Match([NamedUse.Once("reviewer", Monday)], null, Monday, [reviewer]));

        Assert.Equal(reviewer.Id, used.AgentId);
        Assert.Null(used.Version);
    }

    [Fact]
    public void Without_an_end_time_the_version_is_unknown()
    {
        var reviewer = AgentWithVersionsAt("reviewer", null, Monday);

        Assert.Null(Assert.Single(Match([new NamedUse("reviewer", 1, null, null)], null, null, [reviewer])).Version);
    }

    [Fact]
    public void A_deleted_agent_is_listed_as_deleted_with_no_agent_or_version()
    {
        var gone = AgentWithVersionsAt("reviewer", null, Monday);
        var tombstone = AgentTombstone.Deleted(gone, 1, Monday.AddDays(1));

        var used = Assert.Single(Match([NamedUse.Once("reviewer", Monday)], null, Monday, [], [tombstone]));

        Assert.True(used.Deleted);
        Assert.Null(used.AgentId);
        Assert.Null(used.Version);
    }

    [Fact]
    public void A_renamed_agent_matches_under_its_new_name()
    {
        var agent = AgentWithVersionsAt("reviewer", null, Monday);
        agent.Rename(Owner, "code-reviewer", []);
        var tombstone = AgentTombstone.Renamed(agent, "reviewer", 1, Monday.AddDays(1));

        var used = Assert.Single(Match([NamedUse.Once("reviewer", Monday)], null, Monday, [agent], [tombstone]));

        Assert.Equal("reviewer", used.Name);
        Assert.Equal(agent.Id, used.AgentId);
        Assert.Equal(1, used.Version);
        Assert.False(used.Deleted);
    }

    [Fact]
    public void A_live_project_agent_wins_over_a_deleted_personal_name_and_a_project_tombstone_over_a_live_personal_agent()
    {
        var inProject = AgentWithVersionsAt("reviewer", Project, Monday);
        var personalGone = AgentTombstone.Deleted(AgentWithVersionsAt("reviewer", null, Monday), 1, Monday);
        Assert.Equal(inProject.Id, Assert.Single(Match([NamedUse.Once("reviewer", Monday)], Project, Monday, [inProject], [personalGone])).AgentId);

        var personal = AgentWithVersionsAt("reviewer", null, Monday);
        var projectGone = AgentTombstone.Deleted(AgentWithVersionsAt("reviewer", Project, Monday), 1, Monday);
        Assert.True(Assert.Single(Match([NamedUse.Once("reviewer", Monday)], Project, Monday, [personal], [projectGone])).Deleted);
    }

    [Fact]
    public void Agents_are_returned_earliest_first_used_first_then_by_name()
    {
        var agents = new[] { AgentWithVersionsAt("b", null, Monday), AgentWithVersionsAt("a", null, Monday), AgentWithVersionsAt("c", null, Monday) };
        NamedUse[] uses = [NamedUse.Once("c", Monday.AddHours(2)), NamedUse.Once("b", Monday.AddHours(1)), NamedUse.Once("a", Monday.AddHours(1))];

        Assert.Equal(["a", "b", "c"], Match(uses, null, Monday.AddHours(3), agents).Select(a => a.Name));
    }
}
