namespace Protostar.Registry.Api.Sessions;

/// <summary>The rule for which stored chunk blobs are orphans that the sweep may delete.</summary>
/// <remarks>
/// <para>
/// An append writes its chunk's blob before the chunk row commits, and a lost race leaves that blob with no
/// row (see <c>TranscriptService.AppendAsync</c>). A blob is an orphan when it is under
/// <see cref="TranscriptChunk.BlobKeyPrefix"/>, no chunk row references it, and the store last wrote it more
/// than the grace period ago.
/// </para>
/// <para>
/// The age check is what keeps an append in progress safe: its blob has no row yet. The grace period
/// therefore has to cover the longest append plus any clock skew between the API host that supplies
/// <c>now</c> and the store that stamped the blob's last-modified time.
/// </para>
/// <para>
/// Chunk rows of earlier revisions are kept when a transcript starts a new revision, so their blobs stay
/// referenced and are never chosen here.
/// </para>
/// </remarks>
public static class OrphanChunkBlobPolicy
{
    /// <summary>The keys in <paramref name="listed"/> that are orphans, given the subset of keys chunk rows reference.</summary>
    /// <param name="listed">Blobs as the store listed them, with the store's last-modified time for each.</param>
    /// <param name="referenced">Keys among <paramref name="listed"/> that some chunk row references.</param>
    /// <param name="now">The current time.</param>
    /// <param name="grace">How old an unreferenced blob must be before it is an orphan; not negative.</param>
    public static IReadOnlyList<string> SelectOrphans(
        IEnumerable<(string Key, DateTimeOffset LastModified)> listed, IReadOnlySet<string> referenced, DateTimeOffset now, TimeSpan grace)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(grace, TimeSpan.Zero);
        var cutoff = now - grace;
        return listed
            .Where(b => b.Key.StartsWith(TranscriptChunk.BlobKeyPrefix, StringComparison.Ordinal))
            .Where(b => b.LastModified < cutoff)
            .Where(b => !referenced.Contains(b.Key))
            .Select(b => b.Key)
            .ToList();
    }
}
