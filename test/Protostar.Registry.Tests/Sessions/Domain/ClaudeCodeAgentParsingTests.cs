using System.Text;
using Protostar.Registry.Api.Sessions;
using Xunit;

namespace Protostar.Registry.Tests.Sessions.Domain;

/// <summary>Contract tests for how <see cref="ClaudeCodeTranscriptParser"/> records the agent types a transcript delegated to.</summary>
public sealed class ClaudeCodeAgentParsingTests
{
    private static readonly ClaudeCodeTranscriptParser Parser = new();
    private static readonly DateTimeOffset T1 = DateTimeOffset.Parse("2026-11-09T22:00:00Z");
    private static readonly DateTimeOffset T2 = DateTimeOffset.Parse("2026-11-09T23:00:00Z");

    private static TranscriptFacts Parse(params string[] lines) =>
        Parser.Parse(Encoding.UTF8.GetBytes(string.Concat(lines.Select(l => l + "\n"))));

    private static string Call(string tool, string input, DateTimeOffset at) =>
        $$$"""{"type":"assistant","timestamp":"{{{at:O}}}","message":{"role":"assistant","content":[{"type":"tool_use","id":"t1","name":"{{{tool}}}","input":{{{input}}}}]}}""";

    private static string AgentCall(string agentType, DateTimeOffset at, string tool = "Agent") =>
        Call(tool, $$$"""{"subagent_type":"{{{agentType}}}","description":"Review","prompt":"PROMPT-TEXT"}""", at);

    [Fact]
    public void An_Agent_call_counts_by_its_subagent_type()
    {
        var use = Assert.Single(Parse(AgentCall("reviewer", T1)).Agents);

        Assert.Equal(new NamedUse("reviewer", 1, T1, T1), use);
    }

    [Fact]
    public void A_Task_call_from_an_older_harness_counts_the_same_and_adds_up_with_Agent_calls()
    {
        var use = Assert.Single(Parse(AgentCall("reviewer", T2, tool: "Task"), AgentCall("reviewer", T1)).Agents);

        Assert.Equal(new NamedUse("reviewer", 2, T1, T2), use);
    }

    [Fact]
    public void Built_in_and_plugin_types_are_recorded_as_written_for_matching_to_decide()
    {
        var agents = Parse(AgentCall("general-purpose", T1), AgentCall("superpowers:reviewer", T1), AgentCall("Explore", T1)).Agents;

        Assert.Equal(["general-purpose", "superpowers:reviewer", "Explore"], agents.Select(a => a.Name));
    }

    [Fact]
    public void A_call_without_a_subagent_type_is_not_counted()
    {
        Assert.Empty(Parse(Call("Agent", """{"description":"Look around","prompt":"go"}""", T1)).Agents);
    }

    [Fact]
    public void Agents_and_skills_are_kept_apart_so_a_shared_name_is_not_confused()
    {
        var facts = Parse(AgentCall("review", T1), Call("Skill", """{"skill":"review"}""", T1));

        Assert.Equal("review", Assert.Single(facts.Agents).Name);
        Assert.Equal("review", Assert.Single(facts.Skills).Name);
    }

    [Fact]
    public void The_prompt_an_agent_was_given_is_never_kept_in_the_summary()
    {
        var summary = TranscriptSummary.Empty.Add(Parse(AgentCall("reviewer", T1)));

        Assert.DoesNotContain("PROMPT-TEXT", System.Text.Json.JsonSerializer.Serialize(summary));
        Assert.Equal("reviewer", Assert.Single(summary.Agents).Name);
    }

    [Fact]
    public void Summaries_fold_agents_across_chunks()
    {
        var summary = TranscriptSummary.Empty.Add(Parse(AgentCall("reviewer", T1))).Add(Parse(AgentCall("reviewer", T2), AgentCall("planner", T2)));

        Assert.Equal([new NamedUse("reviewer", 2, T1, T2), new NamedUse("planner", 1, T2, T2)], summary.Agents);
    }
}
