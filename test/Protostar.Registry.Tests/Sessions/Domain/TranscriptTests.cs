using System.Text;
using Protostar.Registry.Api.Infrastructure;
using Protostar.Registry.Api.Sessions;
using Xunit;

namespace Protostar.Registry.Tests.Sessions.Domain;

/// <summary>Contract tests for the <see cref="Transcript"/> aggregate's append rules.</summary>
public sealed class TranscriptTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);
    private static readonly Guid Machine = Guid.NewGuid();

    private static Transcript NewSession()
    {
        HarnessId.TryCreate("claude-code", out var harness);
        TranscriptKey.TryCreate("6f83525b-790f-4654-944d-ba3b0c96453e", out var key);
        return Transcript.Register(Guid.NewGuid(), harness, key, TranscriptKind.Session, null, Machine, "/home/me/.claude/projects/p/s.jsonl", null, Now);
    }

    private static byte[] Lines(params string[] lines) => Encoding.UTF8.GetBytes(string.Concat(lines.Select(l => l + "\n")));

    private static TranscriptAppendOutcome Append(Transcript t, int revision, long offset, byte[] content) =>
        t.Append(revision, offset, content, "blob", content.Length / 2, Machine, LineCountingParser.Instance, Now);

    [Fact]
    public void A_new_transcript_is_empty_at_revision_one()
    {
        var t = NewSession();

        Assert.Equal(1, t.Revision);
        Assert.Equal(0, t.Length);
        Assert.Equal(0, t.ChunkCount);
    }

    [Fact]
    public void Appending_at_the_current_position_extends_the_transcript()
    {
        var t = NewSession();
        var first = Lines("{}", "{}");
        var second = Lines("{}");

        var a = Append(t, 1, 0, first);
        var b = Append(t, 1, first.Length, second);

        Assert.NotNull(a.Chunk);
        Assert.Equal(first.Length, b.Chunk!.Offset);
        Assert.Equal(first.Length + second.Length, t.Length);
        Assert.Equal(2, t.ChunkCount);
        Assert.Equal(3, t.Summary.Lines);
    }

    [Fact]
    public void A_chunk_records_its_position_hash_and_blob()
    {
        var t = NewSession();
        var content = Lines("{\"a\":1}");

        var chunk = Append(t, 1, 0, content).Chunk!;

        Assert.Equal(t.Id, chunk.TranscriptId);
        Assert.Equal(1, chunk.Revision);
        Assert.Equal(0, chunk.Offset);
        Assert.Equal(content.Length, chunk.Length);
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(content)), chunk.Sha256);
        Assert.Equal("blob", chunk.BlobKey);
        Assert.Equal(Machine, chunk.MachineId);
    }

    [Theory]
    [InlineData(1, 5)]   // a gap
    [InlineData(1, 0)]   // a repeat of what is already stored
    [InlineData(3, 0)]   // skipping a revision
    [InlineData(0, 0)]   // an old revision
    public void A_chunk_not_at_the_current_position_is_rejected_with_that_position(int revision, long offset)
    {
        var t = NewSession();
        var first = Lines("{}");
        Append(t, 1, 0, first);

        var outcome = Append(t, revision, offset, Lines("{}"));

        Assert.Null(outcome.Chunk);
        Assert.Equal((1, (long)first.Length), outcome.Expected);
        Assert.Equal(first.Length, t.Length);
    }

    [Theory]
    [InlineData(1, 3, true)]    // the current revision at its length
    [InlineData(2, 0, true)]    // the next revision from the start
    [InlineData(1, 0, false)]   // a repeat
    [InlineData(1, 5, false)]   // a gap
    [InlineData(2, 3, false)]   // the next revision must start at 0
    [InlineData(3, 0, false)]   // skipping a revision
    public void A_chunk_is_accepted_only_at_the_current_position_or_a_fresh_revision(int revision, long offset, bool accepted)
    {
        var t = NewSession();
        Append(t, 1, 0, Lines("{}"));

        Assert.Equal(accepted, t.AcceptsChunkAt(revision, offset));
    }

    [Fact]
    public void The_next_revision_at_offset_zero_starts_over()
    {
        var t = NewSession();
        Append(t, 1, 0, Lines("{}", "{}", "{}"));
        var rewritten = Lines("{}");

        var outcome = Append(t, 2, 0, rewritten);

        Assert.NotNull(outcome.Chunk);
        Assert.Equal(2, t.Revision);
        Assert.Equal(rewritten.Length, t.Length);
        Assert.Equal(1, t.Summary.Lines);
        Assert.Equal(2, t.ChunkCount);
    }

    [Fact]
    public void A_chunk_must_end_at_a_line_boundary()
    {
        var t = NewSession();

        var outcome = Append(t, 1, 0, Encoding.UTF8.GetBytes("{\"half\":"));

        Assert.Contains("line boundary", outcome.Error);
        Assert.Equal(0, t.Length);
    }

    [Fact]
    public void An_empty_chunk_is_rejected()
    {
        var outcome = Append(NewSession(), 1, 0, []);

        Assert.NotNull(outcome.Error);
    }

    [Fact]
    public void A_chunk_over_the_size_limit_is_rejected()
    {
        var big = new byte[Transcript.MaxChunkBytes + 1];
        big[^1] = (byte)'\n';

        var outcome = Append(NewSession(), 1, 0, big);

        Assert.Contains("at most", outcome.Error);
    }

    [Fact]
    public void A_subagent_must_name_its_session()
    {
        HarnessId.TryCreate("claude-code", out var harness);
        TranscriptKey.TryCreate("s.agent-1", out var key);

        Assert.Throws<ArgumentException>(() =>
            Transcript.Register(Guid.NewGuid(), harness, key, TranscriptKind.Subagent, null, Machine, "/p", null, Now));
    }
}

/// <summary>Contract tests for <see cref="HarnessId"/> and <see cref="TranscriptKey"/>.</summary>
public sealed class TranscriptIdentifierTests
{
    [Theory]
    [InlineData("claude-code", true)]
    [InlineData("codex", true)]
    [InlineData("Claude-Code", false)]
    [InlineData("", false)]
    [InlineData("-leading", false)]
    [InlineData("has space", false)]
    public void Harness_ids_are_lowercase_letters_digits_and_dashes(string raw, bool valid) =>
        Assert.Equal(valid, HarnessId.TryCreate(raw, out _));

    [Theory]
    [InlineData("6f83525b-790f-4654-944d-ba3b0c96453e", true)]
    [InlineData("49ae618f-8830-41e4-ae28-90e4d9b35336.agent-ae75b028226c93701", true)]
    [InlineData("../etc/passwd", false)]
    [InlineData("a/b", false)]
    [InlineData(".hidden", false)]
    [InlineData("", false)]
    public void Transcript_keys_are_url_safe_and_cannot_traverse(string raw, bool valid) =>
        Assert.Equal(valid, TranscriptKey.TryCreate(raw, out _));
}

/// <summary>Contract tests for <see cref="ClaudeCodeTranscriptParser"/> and how <see cref="TranscriptSummary"/> folds its facts.</summary>
public sealed class ClaudeCodeTranscriptParserTests
{
    private static readonly ClaudeCodeTranscriptParser Parser = new();

    private static TranscriptFacts Parse(params string[] lines) =>
        Parser.Parse(Encoding.UTF8.GetBytes(string.Concat(lines.Select(l => l + "\n"))));

    private const string Prompt =
        """{"type":"user","timestamp":"2026-11-09T22:49:28.840Z","cwd":"/home/me/src/protostar","gitBranch":"main","version":"2.1.321","message":{"role":"user","content":"Spec out the CRM"}}""";

    private const string ToolResult =
        """{"type":"user","timestamp":"2026-11-09T22:49:40.000Z","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"t1","content":"ok"}]}}""";

    private const string Reply =
        """{"type":"assistant","timestamp":"2026-11-09T22:49:32.356Z","version":"2.1.322","message":{"role":"assistant","content":[{"type":"text","text":"Sure"}]}}""";

    [Fact]
    public void Reads_prompt_reply_and_context_from_message_lines()
    {
        var facts = Parse(Prompt, Reply);

        Assert.Equal(1, facts.UserMessages);
        Assert.Equal(1, facts.AssistantMessages);
        Assert.Equal("Spec out the CRM", facts.FirstPrompt);
        Assert.Equal("/home/me/src/protostar", facts.WorkingDirectory);
        Assert.Equal("main", facts.GitBranch);
        Assert.Equal("2.1.322", facts.HarnessVersion);
        Assert.Equal(DateTimeOffset.Parse("2026-11-09T22:49:28.840Z"), facts.FirstTimestamp);
        Assert.Equal(DateTimeOffset.Parse("2026-11-09T22:49:32.356Z"), facts.LastTimestamp);
        Assert.Equal(2, facts.Lines);
    }

    [Fact]
    public void Tool_results_and_meta_lines_are_not_prompts()
    {
        var meta = """{"type":"user","isMeta":true,"message":{"role":"user","content":"<command-name>/clear</command-name>"}}""";

        var facts = Parse(ToolResult, meta);

        Assert.Equal(0, facts.UserMessages);
        Assert.Null(facts.FirstPrompt);
    }

    [Fact]
    public void Prompt_text_can_come_from_a_text_block()
    {
        var facts = Parse("""{"type":"user","message":{"role":"user","content":[{"type":"text","text":"From a block"}]}}""");

        Assert.Equal("From a block", facts.FirstPrompt);
    }

    [Fact]
    public void Reads_the_ai_title()
    {
        var facts = Parse("""{"type":"ai-title","aiTitle":"Void CRM system spec"}""");

        Assert.Equal("Void CRM system spec", facts.Title);
    }

    [Fact]
    public void Unknown_types_and_bad_lines_are_counted_not_fatal()
    {
        var facts = Parse("""{"type":"queue-operation","operation":"enqueue"}""", "{not json", "");

        Assert.Equal(3, facts.Lines);
        Assert.Equal(1, facts.MalformedLines);
        Assert.Equal(0, facts.UserMessages);
    }

    [Fact]
    public void A_long_prompt_is_shortened_to_one_line()
    {
        var text = string.Join("\n", Enumerable.Repeat("word", 200));
        var line = $$$"""{"type":"user","message":{"role":"user","content":{{{System.Text.Json.JsonSerializer.Serialize(text)}}}}}""";

        var prompt = Parse(line).FirstPrompt!;

        Assert.True(prompt.Length <= 200);
        Assert.DoesNotContain('\n', prompt);
        Assert.EndsWith("...", prompt);
    }

    [Fact]
    public void Timestamps_with_a_non_UTC_offset_keep_their_instant()
    {
        // Claude Code writes Z, but another harness or a format change may not (#56).
        var facts = Parse(
            """{"type":"user","timestamp":"2026-11-09T16:49:28.840-06:00","message":{"role":"user","content":"Spec out the CRM"}}""",
            """{"type":"assistant","timestamp":"2026-11-09T16:49:32.356-06:00","message":{"role":"assistant","content":[{"type":"text","text":"Sure"}]}}""");

        Assert.Equal(DateTimeOffset.Parse("2026-11-09T22:49:28.840Z"), facts.FirstTimestamp);
        Assert.Equal(DateTimeOffset.Parse("2026-11-09T22:49:32.356Z"), facts.LastTimestamp);

        // The parser keeps the offset it read; the persistence convention stores it as UTC.
        var stored = (DateTimeOffset)new UtcDateTimeOffsetConverter().ConvertToProvider(facts.FirstTimestamp!.Value)!;
        Assert.Equal(TimeSpan.Zero, stored.Offset);
    }

    [Fact]
    public void Summary_keeps_the_first_prompt_and_latest_title_and_accumulates_counts()
    {
        var summary = TranscriptSummary.Empty
            .Add(Parse(Prompt, """{"type":"ai-title","aiTitle":"Draft title"}"""))
            .Add(Parse("""{"type":"user","timestamp":"2026-11-10T09:00:00Z","message":{"role":"user","content":"Second ask"}}""", Reply,
                """{"type":"ai-title","aiTitle":"Final title"}"""));

        Assert.Equal("Spec out the CRM", summary.FirstPrompt);
        Assert.Equal("Final title", summary.Title);
        Assert.Equal(2, summary.UserMessages);
        Assert.Equal(1, summary.AssistantMessages);
        Assert.Equal(DateTimeOffset.Parse("2026-11-09T22:49:28.840Z"), summary.StartedAt);
        Assert.Equal(DateTimeOffset.Parse("2026-11-10T09:00:00Z"), summary.LastActivityAt);
        Assert.Equal(5, summary.Lines);
    }
}
