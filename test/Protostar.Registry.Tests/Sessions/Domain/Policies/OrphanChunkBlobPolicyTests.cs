using Protostar.Registry.Api.Sessions;
using Xunit;

namespace Protostar.Registry.Tests.Sessions.Domain.Policies;

/// <summary>Contract tests for <see cref="OrphanChunkBlobPolicy"/>: which listed blobs the sweep may delete.</summary>
public sealed class OrphanChunkBlobPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Grace = TimeSpan.FromHours(24);
    private static readonly DateTimeOffset LongAgo = Now - TimeSpan.FromDays(30);

    private static string ChunkKey(string name) => $"transcripts/owner/transcript/r1/{name}.jsonl.gz";

    private static IReadOnlyList<string> Select(IEnumerable<(string, DateTimeOffset)> listed, params string[] referenced) =>
        OrphanChunkBlobPolicy.SelectOrphans(listed, referenced.ToHashSet(), Now, Grace);

    [Fact]
    public void An_old_unreferenced_chunk_blob_is_an_orphan()
    {
        var orphan = ChunkKey("orphan");

        Assert.Equal([orphan], Select([(orphan, LongAgo)]));
    }

    [Fact]
    public void A_referenced_blob_is_never_an_orphan_however_old()
    {
        var referenced = ChunkKey("kept");

        Assert.Empty(Select([(referenced, DateTimeOffset.MinValue)], referenced));
    }

    [Fact]
    public void A_referenced_blob_from_an_earlier_revision_is_never_an_orphan()
    {
        var earlier = "transcripts/owner/transcript/r1/0000000000000000-a.jsonl.gz";
        var current = "transcripts/owner/transcript/r2/0000000000000000-b.jsonl.gz";

        Assert.Empty(Select([(earlier, LongAgo), (current, LongAgo)], earlier, current));
    }

    [Fact]
    public void An_unreferenced_blob_younger_than_the_grace_period_is_not_an_orphan()
    {
        // Its append may still be about to commit the row that references it.
        var inFlight = ChunkKey("in-flight");

        Assert.Empty(Select([(inFlight, Now - Grace + TimeSpan.FromSeconds(1))]));
    }

    [Fact]
    public void An_unreferenced_blob_exactly_at_the_grace_period_is_not_yet_an_orphan()
    {
        Assert.Empty(Select([(ChunkKey("boundary"), Now - Grace)]));
    }

    [Fact]
    public void A_blob_stamped_in_the_future_by_a_skewed_store_clock_is_not_an_orphan()
    {
        Assert.Empty(Select([(ChunkKey("future"), Now + TimeSpan.FromHours(1))]));
    }

    [Theory]
    [InlineData("skills/owner/skill/file.bin")]
    [InlineData("transcripts")]
    [InlineData("Transcripts/owner/transcript/r1/0-a.jsonl.gz")]
    [InlineData("backup/transcripts/owner/transcript/r1/0-a.jsonl.gz")]
    public void A_blob_outside_the_transcripts_prefix_is_never_touched(string key)
    {
        Assert.Empty(Select([(key, LongAgo)]));
    }

    [Fact]
    public void Only_the_orphans_are_chosen_from_a_mixed_page()
    {
        var orphan = ChunkKey("orphan");
        var referenced = ChunkKey("referenced");
        var young = ChunkKey("young");

        var chosen = Select(
            [(orphan, LongAgo), (referenced, LongAgo), (young, Now), ("other/old", LongAgo)],
            referenced);

        Assert.Equal([orphan], chosen);
    }

    [Fact]
    public void A_zero_grace_period_chooses_any_unreferenced_blob_written_before_now()
    {
        var orphan = ChunkKey("orphan");

        Assert.Equal([orphan], OrphanChunkBlobPolicy.SelectOrphans([(orphan, Now - TimeSpan.FromSeconds(1))], new HashSet<string>(), Now, TimeSpan.Zero));
    }

    [Fact]
    public void A_negative_grace_period_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            OrphanChunkBlobPolicy.SelectOrphans([], new HashSet<string>(), Now, TimeSpan.FromSeconds(-1)));
    }
}
