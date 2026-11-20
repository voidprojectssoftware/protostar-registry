namespace Protostar.Registry.Api.Search;

/// <summary>What a search entry was made from.</summary>
public enum SearchSource
{
    /// <summary>A chunk of a session or subagent transcript.</summary>
    Session,

    /// <summary>The current version of a memory file.</summary>
    Memory,
}

/// <summary>One piece of redacted, searchable text from a transcript chunk or a memory file, in the owner's search index.</summary>
/// <remarks>
/// <para>
/// The index is a projection: everything in it can be rebuilt from the transcripts and memory the registry keeps.
/// Its text is always redacted (<see cref="SecretRedactor"/>), because the factories here are the only way to make an
/// entry and they redact before anything else, so neither the full-text vector nor a snippet cut from the text can
/// hold a secret the redactor knows.
/// </para>
/// <para>
/// A source yields at least one entry, even when it has no text, so "indexed and empty" is told apart from "not
/// indexed yet" and the backfill finds only what is missing. Long text is split into segments of at most
/// <see cref="MaxSegmentLength"/> characters, because a Postgres <c>tsvector</c> is limited to 1 MB.
/// </para>
/// </remarks>
public sealed class SearchEntry
{
    /// <summary>The version of the text extraction and redaction that made an entry.</summary>
    /// <remarks>
    /// Bump it whenever what is indexed changes: a parser's text extraction or the redactor's patterns. Entries of an
    /// older version count as missing, so the backfill re-indexes them. 2: the redactor replaces a secret-named value
    /// whole and reads names through escaped quotes (registry#78, #79), and redacts bearer tokens.
    /// </remarks>
    public const int CurrentIndexVersion = 2;

    /// <summary>The most characters of text one entry holds.</summary>
    public const int MaxSegmentLength = 32_000;

    private SearchEntry()
    {
    }

    public Guid Id { get; private set; }

    public Guid OwnerId { get; private set; }

    public SearchSource Source { get; private set; }

    /// <summary>For a session entry, the transcript; <c>null</c> for memory.</summary>
    public Guid? TranscriptId { get; private set; }

    /// <summary>For a session entry, the chunk the text came from; <c>null</c> for memory.</summary>
    public Guid? ChunkId { get; private set; }

    /// <summary>For a memory entry, the file; <c>null</c> for a session.</summary>
    public Guid? MemoryFileId { get; private set; }

    /// <summary>For a memory entry, the file's <c>UpdatedAt</c> when it was indexed; a later change makes the entry stale.</summary>
    public DateTimeOffset? MemoryFileUpdatedAt { get; private set; }

    /// <summary>The position of this piece among its source's entries, from 0.</summary>
    public int Segment { get; private set; }

    /// <summary>The machine that sent the text: the chunk's sender, or the machine that first sent the memory version.</summary>
    public Guid MachineId { get; private set; }

    /// <summary>The redacted text.</summary>
    public string Text { get; private set; } = default!;

    /// <summary>The <see cref="CurrentIndexVersion"/> the entry was made with.</summary>
    public int IndexVersion { get; private set; }

    public DateTimeOffset IndexedAt { get; private set; }

    /// <summary>Entries for the readable text of one transcript chunk.</summary>
    /// <param name="ownerId">The transcript's owner.</param>
    /// <param name="transcriptId">The transcript.</param>
    /// <param name="chunkId">The chunk.</param>
    /// <param name="machineId">The machine that sent the chunk.</param>
    /// <param name="text">The chunk's readable text, as its harness's parser extracted it, not yet redacted.</param>
    /// <param name="now">The time of indexing.</param>
    public static IReadOnlyList<SearchEntry> ForChunk(Guid ownerId, Guid transcriptId, Guid chunkId, Guid machineId, string? text, DateTimeOffset now) =>
        Segments(text).Select((segment, i) => new SearchEntry
        {
            Id = Guid.NewGuid(),
            OwnerId = ownerId,
            Source = SearchSource.Session,
            TranscriptId = transcriptId,
            ChunkId = chunkId,
            Segment = i,
            MachineId = machineId,
            Text = segment,
            IndexVersion = CurrentIndexVersion,
            IndexedAt = now,
        }).ToList();

    /// <summary>Entries for one memory file's current version.</summary>
    /// <param name="ownerId">The file's owner.</param>
    /// <param name="memoryFileId">The file.</param>
    /// <param name="fileUpdatedAt">The file's <c>UpdatedAt</c> as loaded, so a later change is seen as stale.</param>
    /// <param name="machineId">The machine that first sent the version indexed.</param>
    /// <param name="text">The version's text, not yet redacted; <c>null</c> when there is nothing to search (deleted everywhere, or not text).</param>
    /// <param name="now">The time of indexing.</param>
    public static IReadOnlyList<SearchEntry> ForMemoryFile(Guid ownerId, Guid memoryFileId, DateTimeOffset fileUpdatedAt, Guid machineId, string? text, DateTimeOffset now) =>
        Segments(text).Select((segment, i) => new SearchEntry
        {
            Id = Guid.NewGuid(),
            OwnerId = ownerId,
            Source = SearchSource.Memory,
            MemoryFileId = memoryFileId,
            MemoryFileUpdatedAt = fileUpdatedAt,
            Segment = i,
            MachineId = machineId,
            Text = segment,
            IndexVersion = CurrentIndexVersion,
            IndexedAt = now,
        }).ToList();

    /// <summary>Redacts <paramref name="text"/> and splits it into pieces of at most <see cref="MaxSegmentLength"/> characters; one empty piece for no text.</summary>
    /// <remarks>
    /// Redaction runs on the whole text before splitting, so a secret is never cut in two and missed. A piece ends at
    /// the last line break, or failing that the last space, before the limit, so words stay whole where possible.
    /// </remarks>
    public static IReadOnlyList<string> Segments(string? text)
    {
        var redacted = SecretRedactor.Redact((text ?? string.Empty).Replace('\0', ' '));
        if (redacted.Length <= MaxSegmentLength)
            return [redacted];

        var segments = new List<string>();
        var rest = redacted.AsSpan();
        while (rest.Length > MaxSegmentLength)
        {
            var window = rest[..MaxSegmentLength];
            var cut = window.LastIndexOf('\n');
            if (cut <= 0)
                cut = window.LastIndexOf(' ');
            if (cut <= 0)
                cut = MaxSegmentLength;
            segments.Add(rest[..cut].ToString());
            rest = rest[cut..].TrimStart();
        }

        if (!rest.IsEmpty)
            segments.Add(rest.ToString());
        return segments;
    }
}
