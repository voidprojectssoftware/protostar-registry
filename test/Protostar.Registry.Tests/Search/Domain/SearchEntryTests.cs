using Protostar.Registry.Api.Search;

namespace Protostar.Registry.Tests.Search.Domain;

/// <summary>Contract tests for <see cref="SearchEntry"/>: entries are redacted, bounded, and never missing for a source.</summary>
public sealed class SearchEntryTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 19, 10, 0, 0, TimeSpan.Zero);
    private static readonly string Token = "gh" + "p_" + string.Concat(Enumerable.Repeat("Zx9Kq2Lm4N", 4))[..36];

    [Fact]
    public void A_chunk_entry_holds_redacted_text_only()
    {
        var entries = SearchEntry.ForChunk(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), $"rotate {Token} now", Now);

        var entry = Assert.Single(entries);
        Assert.Equal("rotate [redacted:github-token] now", entry.Text);
        Assert.Equal(SearchSource.Session, entry.Source);
        Assert.Equal(SearchEntry.CurrentIndexVersion, entry.IndexVersion);
    }

    [Fact]
    public void A_memory_entry_holds_redacted_text_and_the_file_change_it_was_made_from()
    {
        var updatedAt = Now.AddMinutes(-5);

        var entry = Assert.Single(SearchEntry.ForMemoryFile(Guid.NewGuid(), Guid.NewGuid(), updatedAt, Guid.NewGuid(), "password=hunter2-supersecret", Now));

        Assert.Equal("password=[redacted:secret]", entry.Text);
        Assert.Equal(SearchSource.Memory, entry.Source);
        Assert.Equal(updatedAt, entry.MemoryFileUpdatedAt);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void A_source_with_no_text_still_gets_one_empty_entry(string? text)
    {
        var entry = Assert.Single(SearchEntry.ForChunk(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), text, Now));

        Assert.Equal(string.Empty, entry.Text);
        Assert.Equal(0, entry.Segment);
    }

    [Fact]
    public void Long_text_is_split_into_numbered_segments_within_the_limit_without_losing_words()
    {
        var words = Enumerable.Range(0, 20_000).Select(i => $"word{i}").ToList();
        var text = string.Join(' ', words);

        var entries = SearchEntry.ForChunk(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), text, Now);

        Assert.True(entries.Count > 1);
        Assert.All(entries, e => Assert.True(e.Text.Length <= SearchEntry.MaxSegmentLength));
        Assert.Equal(Enumerable.Range(0, entries.Count), entries.Select(e => e.Segment));
        Assert.Equal(words, entries.SelectMany(e => e.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries)));
    }

    [Fact]
    public void A_secret_across_a_segment_boundary_is_still_redacted()
    {
        var text = new string('a', SearchEntry.MaxSegmentLength - 10) + " " + Token + " tail";

        var segments = SearchEntry.Segments(text);

        Assert.DoesNotContain(segments, s => s.Contains("Zx9Kq2Lm4N", StringComparison.Ordinal));
        Assert.Contains(segments, s => s.Contains("[redacted:github-token]", StringComparison.Ordinal));
    }
}
