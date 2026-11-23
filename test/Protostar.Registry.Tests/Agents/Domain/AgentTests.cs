using System.Text;
using Protostar.Registry.Api.Agents;
using Protostar.Registry.Api.Skills;

namespace Protostar.Registry.Tests.Agents.Domain;

/// <summary>Contract tests for the <see cref="Agent"/> aggregate: the versioning rules it copies from skills (ADR 0003).</summary>
public sealed class AgentTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;
    private static readonly AgentManifest Manifest = new() { Name = "reviewer", Description = "Reviews code." };
    private static readonly RelativePath FilePath = RelativePath.FromTrusted("review/reviewer.md");

    private static Agent NewAgent(out Guid creator, Guid? projectId = null)
    {
        creator = Guid.NewGuid();
        return Agent.Create(creator, "reviewer", Now, projectId);
    }

    private static AgentVersionPushOutcome Push(Agent agent, Guid pusher, string text, int? baseVersion = null, RelativePath? path = null) =>
        agent.PushVersion(pusher, Manifest, path ?? FilePath, Encoding.UTF8.GetBytes(text), Now, baseVersion);

    [Fact]
    public void The_first_push_is_version_1_with_the_file_its_path_and_its_metadata_and_raises_an_event()
    {
        var agent = NewAgent(out var creator);

        var outcome = agent.PushVersion(creator, Manifest with { Tools = ["Read", "Grep"], Model = "sonnet" }, FilePath, "hello"u8.ToArray(), Now);

        Assert.False(outcome.IsUnchanged);
        Assert.Equal(1, outcome.Version.VersionNumber);
        Assert.Same(outcome.Version, agent.CurrentVersion);
        Assert.Equal("review/reviewer.md", outcome.Version.Path.Value);
        Assert.Equal("hello"u8.ToArray(), outcome.Version.Content);
        Assert.Equal(5, outcome.Version.Size);
        Assert.Equal(Sha256Hash.Of("hello"u8.ToArray()), outcome.Version.ContentHash);
        Assert.Equal("Reviews code.", outcome.Version.Description);
        Assert.Equal("[\"Read\",\"Grep\"]", outcome.Version.ToolsJson);
        Assert.Equal("sonnet", outcome.Version.Model);
        var pushed = Assert.IsType<AgentVersionPushed>(Assert.Single(agent.DomainEvents));
        Assert.Equal(1, pushed.VersionNumber);
    }

    [Fact]
    public void Identical_bytes_are_unchanged_even_from_another_path_and_raise_no_event()
    {
        var agent = NewAgent(out var creator);
        Push(agent, creator, "body");
        agent.ClearDomainEvents();

        var again = Push(agent, creator, "body", baseVersion: 0, path: RelativePath.FromTrusted("reviewer.md"));

        Assert.True(again.IsUnchanged);
        Assert.Single(agent.Versions);
        Assert.Empty(agent.DomainEvents);
    }

    [Fact]
    public void A_push_based_on_the_current_version_stores_the_next()
    {
        var agent = NewAgent(out var creator);
        Push(agent, creator, "one");

        Assert.Equal(2, Push(agent, creator, "two", baseVersion: 1).Version.VersionNumber);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    public void A_push_based_on_another_version_is_refused_as_stale_naming_the_current_one(int baseVersion)
    {
        var agent = NewAgent(out var creator);
        Push(agent, creator, "one");
        Push(agent, creator, "two", baseVersion: 1);

        var stale = Assert.Throws<AgentVersionStaleException>(() => Push(agent, creator, "three", baseVersion));

        Assert.Equal(2, stale.CurrentVersion);
        Assert.Equal(2, agent.CurrentVersion!.VersionNumber);
    }

    [Fact]
    public void A_push_with_no_base_is_accepted_on_top_as_older_clients_would()
    {
        var agent = NewAgent(out var creator);
        Push(agent, creator, "one");

        Assert.Equal(2, Push(agent, creator, "two").Version.VersionNumber);
    }

    [Fact]
    public void Only_the_creator_may_push_rename_or_delete()
    {
        var agent = NewAgent(out _);
        var other = Guid.NewGuid();

        Assert.Throws<AgentCreatorViolationException>(() => Push(agent, other, "x"));
        Assert.Throws<AgentCreatorViolationException>(() => agent.Rename(other, "new", []));
        Assert.Throws<AgentCreatorViolationException>(() => agent.EnsureCanDelete(other));
    }

    [Fact]
    public void A_file_over_the_size_limit_is_refused()
    {
        var agent = NewAgent(out var creator);

        Assert.Throws<AgentSizeExceededException>(() =>
            agent.PushVersion(creator, Manifest, FilePath, new byte[AgentSizePolicy.MaxFileBytes + 1], Now));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    public void A_name_that_is_blank_or_has_a_path_separator_cannot_name_an_agent(string name)
    {
        Assert.Throws<ArgumentException>(() => Agent.Create(Guid.NewGuid(), name, Now));
    }

    [Fact]
    public void Rename_keeps_the_history_refuses_a_taken_name_and_is_a_no_op_to_the_same_name()
    {
        var agent = NewAgent(out var creator);
        Push(agent, creator, "one");

        Assert.False(agent.Rename(creator, "reviewer", ["reviewer"]));
        Assert.Throws<AgentNameTakenException>(() => agent.Rename(creator, "linter", ["reviewer", "linter"]));
        Assert.True(agent.Rename(creator, "critic", ["reviewer"]));
        Assert.Equal("critic", agent.Name);
        Assert.Single(agent.Versions);
    }

    [Fact]
    public void A_new_agent_replacing_a_tombstone_numbers_after_it_so_old_copies_are_stale()
    {
        var old = NewAgent(out var creator);
        Push(old, creator, "one");
        Push(old, creator, "two", baseVersion: 1);
        var tombstone = AgentTombstone.Deleted(old, 2, Now);

        var fresh = Agent.Create(creator, "reviewer", Now, replacing: tombstone);

        Assert.Equal(3, Push(fresh, creator, "new", baseVersion: 0).Version.VersionNumber);
        Assert.Throws<AgentVersionStaleException>(() => Push(fresh, creator, "old copy", baseVersion: 2));
    }

    [Fact]
    public void A_personal_agent_never_moves_with_a_folder()
    {
        var agent = NewAgent(out _);

        Assert.Throws<InvalidOperationException>(() => agent.MoveWithFolder(Guid.NewGuid(), null));
    }
}
