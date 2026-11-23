using Protostar.Registry.Api.Agents;

namespace Protostar.Registry.Tests.Agents.Domain;

/// <summary>Contract tests for <see cref="AgentTombstone"/>: which pushes of a removed agent's name come from a copy of it.</summary>
public sealed class AgentTombstoneTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    [Theory]
    [InlineData(null, false)]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(3, true)]
    [InlineData(4, false)]
    public void Refuses_only_a_push_based_on_a_version_of_the_removed_agent(int? baseVersion, bool refused)
    {
        var tombstone = AgentTombstone.Deleted(Agent.Create(Guid.NewGuid(), "reviewer", Now), 3, Now);

        Assert.Equal(refused, tombstone.Refuses(baseVersion));
    }

    [Fact]
    public void A_rename_tombstone_follows_the_agent_and_turns_into_a_delete_with_it()
    {
        var agent = Agent.Create(Guid.NewGuid(), "reviewer", Now);
        agent.Rename(agent.CreatorId, "critic", []);

        var tombstone = AgentTombstone.Renamed(agent, "reviewer", 2, Now);
        Assert.Equal("reviewer", tombstone.Name);
        Assert.Equal("critic", tombstone.RenamedTo);

        tombstone.FollowRename("judge");
        Assert.Equal("judge", tombstone.RenamedTo);

        tombstone.TargetDeleted();
        Assert.Null(tombstone.RenamedTo);
        Assert.True(tombstone.Refuses(2));
    }

    [Fact]
    public void A_rename_tombstone_is_never_for_the_name_the_agent_still_has()
    {
        var agent = Agent.Create(Guid.NewGuid(), "reviewer", Now);

        Assert.Throws<ArgumentException>(() => AgentTombstone.Renamed(agent, "reviewer", 1, Now));
    }
}
