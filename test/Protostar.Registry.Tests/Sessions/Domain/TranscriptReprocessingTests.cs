using System.Text;
using Protostar.Registry.Api.Infrastructure;
using Protostar.Registry.Api.Sessions;
using Xunit;

namespace Protostar.Registry.Tests.Sessions.Domain;

/// <summary>Contract tests for which summaries the reprocessor rebuilds (<see cref="TranscriptSummary.ParserVersion"/>) and how a rebuilt one is applied (<see cref="Transcript.ReplaceSummary"/>).</summary>
public sealed class TranscriptReprocessingTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 19, 10, 0, 0, TimeSpan.Zero);
    private static readonly Guid Machine = Guid.NewGuid();

    private static readonly TranscriptSummary Stale = new() { ParserVersion = TranscriptSummary.CurrentParserVersion - 1, FirstPrompt = "stale" };

    private static Transcript NewSession()
    {
        HarnessId.TryCreate("claude-code", out var harness);
        TranscriptKey.TryCreate("6f83525b-790f-4654-944d-ba3b0c96453e", out var key);
        return Transcript.Register(Guid.NewGuid(), harness, key, TranscriptKind.Session, null, Machine, "/home/me/.claude/projects/p/s.jsonl", null, Now);
    }

    private static byte[] Lines(params string[] lines) => Encoding.UTF8.GetBytes(string.Concat(lines.Select(l => l + "\n")));

    private static void Append(Transcript t, int revision, long offset, byte[] content) =>
        Assert.NotNull(t.Append(revision, offset, content, "blob", content.Length / 2, Machine, LineCountingParser.Instance, Now).Chunk);

    [Fact]
    public void The_empty_summary_is_at_the_current_parser_version()
    {
        Assert.Equal(TranscriptSummary.CurrentParserVersion, TranscriptSummary.Empty.ParserVersion);
        Assert.True(TranscriptSummary.Empty.IsCurrent);
    }

    [Fact]
    public void A_new_transcripts_summary_is_current_so_it_is_not_reprocessed()
    {
        var t = NewSession();
        Append(t, 1, 0, Lines("{}"));

        Assert.True(t.Summary.IsCurrent);
    }

    [Fact]
    public void A_summary_from_an_older_parser_is_not_current() =>
        Assert.False(new TranscriptSummary { ParserVersion = TranscriptSummary.CurrentParserVersion - 1 }.IsCurrent);

    [Fact]
    public void A_summary_stored_before_parser_versions_existed_is_not_current() =>
        Assert.False(new TranscriptSummary { ParserVersion = 0 }.IsCurrent);

    [Fact]
    public void A_summary_from_a_newer_parser_counts_as_current_so_an_older_instance_leaves_it_alone() =>
        Assert.True(new TranscriptSummary { ParserVersion = TranscriptSummary.CurrentParserVersion + 1 }.IsCurrent);

    [Fact]
    public void Appending_to_a_stale_summary_keeps_it_stale()
    {
        var t = NewSession();
        Assert.True(t.ReplaceSummary(Stale, 1, 0));

        Append(t, 1, 0, Lines("{}", "{}"));

        Assert.False(t.Summary.IsCurrent);
        Assert.Equal(2, t.Summary.Lines);
    }

    [Fact]
    public void A_new_revision_starts_a_current_summary()
    {
        var t = NewSession();
        Append(t, 1, 0, Lines("{}"));
        Assert.True(t.ReplaceSummary(Stale, 1, t.Length));

        Append(t, 2, 0, Lines("{}"));

        Assert.True(t.Summary.IsCurrent);
        Assert.Null(t.Summary.FirstPrompt);
    }

    [Fact]
    public void A_rebuilt_summary_replaces_the_old_one_when_it_covers_exactly_what_is_stored()
    {
        var t = NewSession();
        var content = Lines("{}", "{}");
        Append(t, 1, 0, content);
        var rebuilt = TranscriptSummary.Empty.Add(new TranscriptFacts { Lines = 2, FirstPrompt = "rebuilt" });

        Assert.True(t.ReplaceSummary(rebuilt, 1, content.Length));

        Assert.Equal(rebuilt, t.Summary);
        Assert.Equal(content.Length, t.Length);
        Assert.Equal(Now, t.UpdatedAt);
    }

    [Theory]
    [InlineData(1, 3)]  // built before a chunk appended since
    [InlineData(2, 6)]  // a revision the transcript is not at
    [InlineData(1, 9)]  // more than is stored
    public void A_rebuilt_summary_that_does_not_cover_exactly_what_is_stored_is_refused(int revision, long length)
    {
        var t = NewSession();
        Append(t, 1, 0, Lines("{}"));   // 3 bytes
        Append(t, 1, 3, Lines("{}"));   // 6 bytes
        var before = t.Summary;

        Assert.False(t.ReplaceSummary(TranscriptSummary.Empty, revision, length));

        Assert.Same(before, t.Summary);
    }
}
