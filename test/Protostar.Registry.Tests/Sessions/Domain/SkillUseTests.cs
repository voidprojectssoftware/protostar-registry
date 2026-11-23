using System.Text;
using Protostar.Registry.Api.Sessions;
using Xunit;

namespace Protostar.Registry.Tests.Sessions.Domain;

/// <summary>Contract tests for how <see cref="ClaudeCodeTranscriptParser"/> recognises skill invocations.</summary>
public sealed class ClaudeCodeSkillParsingTests
{
    private static readonly ClaudeCodeTranscriptParser Parser = new();
    private static readonly DateTimeOffset T1 = DateTimeOffset.Parse("2026-11-09T22:00:00Z");
    private static readonly DateTimeOffset T2 = DateTimeOffset.Parse("2026-11-09T23:00:00Z");

    private static IReadOnlyList<NamedUse> Skills(params string[] lines) =>
        Parser.Parse(Encoding.UTF8.GetBytes(string.Concat(lines.Select(l => l + "\n")))).Skills;

    private static string SkillCall(string skill, DateTimeOffset at, string args = "") =>
        $$$"""{"type":"assistant","timestamp":"{{{at:O}}}","message":{"role":"assistant","content":[{"type":"text","text":"Loading it"},{"type":"tool_use","id":"t1","name":"Skill","input":{"skill":"{{{skill}}}","args":"{{{args}}}"}}]}}""";

    private static string Command(string name, DateTimeOffset at) =>
        $$$"""{"type":"user","timestamp":"{{{at:O}}}","message":{"role":"user","content":"<command-message>{{{name}}}</command-message>\n<command-name>/{{{name}}}</command-name>"}}""";

    private static string SkillContent(string name) =>
        $$$"""{"type":"user","isMeta":true,"timestamp":"{{{T2:O}}}","message":{"role":"user","content":[{"type":"text","text":"Base directory for this skill: /home/me/.claude/skills/{{{name}}}\n\n# {{{name}}}"}]}}""";

    private const string Reply =
        """{"type":"assistant","timestamp":"2026-11-09T22:30:00Z","message":{"role":"assistant","content":[{"type":"text","text":"Done"}]}}""";

    [Fact]
    public void A_Skill_tool_call_counts_by_its_input_skill_name()
    {
        var use = Assert.Single(Skills(SkillCall("deploy", T1)));

        Assert.Equal(new NamedUse("deploy", 1, T1, T1), use);
    }

    [Fact]
    public void Repeated_calls_add_up_and_widen_the_times()
    {
        var uses = Skills(SkillCall("deploy", T2), SkillCall("review", T1), SkillCall("deploy", T1));

        Assert.Equal([new NamedUse("deploy", 2, T1, T2), new NamedUse("review", 1, T1, T1)], uses);
    }

    [Fact]
    public void A_plugin_skill_keeps_its_full_name()
    {
        Assert.Equal("superpowers:brainstorm", Assert.Single(Skills(SkillCall("superpowers:brainstorm", T1))).Name);
    }

    [Fact]
    public void Other_tools_are_not_skills()
    {
        var grep = """{"type":"assistant","timestamp":"2026-11-09T22:00:00Z","message":{"role":"assistant","content":[{"type":"tool_use","id":"t1","name":"Grep","input":{"skill":"not-a-skill"}}]}}""";

        Assert.Empty(Skills(grep));
    }

    [Fact]
    public void The_arguments_are_never_kept()
    {
        var uses = Skills(SkillCall("deploy", T1, args: "prod --token hunter2"));

        Assert.DoesNotContain("hunter2", string.Join(" ", uses));
    }

    [Fact]
    public void A_slash_command_followed_by_the_skills_content_counts_at_the_commands_time()
    {
        var use = Assert.Single(Skills(Command("review", T1), SkillContent("review")));

        Assert.Equal(new NamedUse("review", 1, T1, T1), use);
    }

    [Fact]
    public void A_slash_command_without_skill_content_is_a_built_in_or_plain_command_and_does_not_count()
    {
        var stdout = """{"type":"user","timestamp":"2026-11-09T22:00:01Z","message":{"role":"user","content":"<local-command-stdout></local-command-stdout>"}}""";

        Assert.Empty(Skills(Command("clear", T1), stdout, Reply));
    }

    [Fact]
    public void Skill_content_that_follows_a_Skill_tool_call_is_not_counted_twice()
    {
        var launched = """{"type":"user","timestamp":"2026-11-09T22:00:01Z","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"t1","content":"Launching skill: deploy"}]}}""";

        var use = Assert.Single(Skills(SkillCall("deploy", T1), launched, SkillContent("deploy")));

        Assert.Equal(1, use.Uses);
    }

    [Fact]
    public void A_command_answered_by_another_line_first_is_no_longer_waiting_for_skill_content()
    {
        Assert.Empty(Skills(Command("clear", T1), Reply, SkillContent("review")));
    }

    [Fact]
    public void Lines_of_other_types_between_a_command_and_its_skill_content_do_not_break_the_pair()
    {
        var snapshot = """{"type":"file-history-snapshot","snapshot":{}}""";

        Assert.Single(Skills(Command("review", T1), snapshot, SkillContent("review")));
    }

    [Fact]
    public void A_name_no_harness_writes_is_skipped()
    {
        Assert.Empty(Skills(SkillCall("", T1), SkillCall("two words", T1), SkillCall(new string('x', NamedUse.MaxNameLength + 1), T1)));
    }

    [Fact]
    public void A_leading_slash_is_not_part_of_the_name()
    {
        Assert.Equal("deploy", Assert.Single(Skills(SkillCall("/deploy", T1))).Name);
    }

    [Fact]
    public void Malformed_lines_are_skipped_and_the_rest_still_count()
    {
        var facts = Parser.Parse(Encoding.UTF8.GetBytes("{not json\n" + SkillCall("deploy", T1) + "\n"));

        Assert.Equal(1, facts.MalformedLines);
        Assert.Single(facts.Skills);
    }

    [Fact]
    public void A_use_without_a_timestamp_has_no_times()
    {
        var untimed = """{"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","id":"t1","name":"Skill","input":{"skill":"deploy"}}]}}""";

        Assert.Equal(new NamedUse("deploy", 1, null, null), Assert.Single(Skills(untimed)));
    }
}

/// <summary>Contract tests for <see cref="NamedUse.Merge"/> and how a summary folds skills in.</summary>
public sealed class SkillUseTests
{
    private static readonly DateTimeOffset T1 = DateTimeOffset.Parse("2026-11-09T22:00:00Z");
    private static readonly DateTimeOffset T2 = DateTimeOffset.Parse("2026-11-09T23:00:00Z");

    [Fact]
    public void Merging_adds_counts_per_name_and_widens_times_keeping_first_seen_order()
    {
        var merged = NamedUse.Merge(
            [new NamedUse("a", 1, T2, T2), new NamedUse("b", 1, T1, T1)],
            [new NamedUse("c", 1, T1, T1), new NamedUse("a", 2, T1, T1)]);

        Assert.Equal([new NamedUse("a", 3, T1, T2), new NamedUse("b", 1, T1, T1), new NamedUse("c", 1, T1, T1)], merged);
    }

    [Fact]
    public void Names_compare_exactly()
    {
        Assert.Equal(2, NamedUse.Merge([NamedUse.Once("Deploy", T1)], [NamedUse.Once("deploy", T1)]).Count);
    }

    [Fact]
    public void Merging_keeps_at_most_the_distinct_limit_and_goes_on_counting_kept_names()
    {
        var full = Enumerable.Range(0, NamedUse.MaxDistinct).Select(i => NamedUse.Once($"s{i}", T1)).ToList();

        var merged = NamedUse.Merge(full, [NamedUse.Once("one-too-many", T1), NamedUse.Once("s0", T2)]);

        Assert.Equal(NamedUse.MaxDistinct, merged.Count);
        Assert.DoesNotContain(merged, u => u.Name == "one-too-many");
        Assert.Equal(new NamedUse("s0", 2, T1, T2), merged[0]);
    }

    [Fact]
    public void A_summary_folds_each_chunks_skills_in()
    {
        var summary = TranscriptSummary.Empty
            .Add(new TranscriptFacts { Skills = [NamedUse.Once("deploy", T1)] })
            .Add(new TranscriptFacts { Skills = [NamedUse.Once("deploy", T2), NamedUse.Once("review", T2)] });

        Assert.Equal([new NamedUse("deploy", 2, T1, T2), NamedUse.Once("review", T2)], summary.Skills);
    }
}
