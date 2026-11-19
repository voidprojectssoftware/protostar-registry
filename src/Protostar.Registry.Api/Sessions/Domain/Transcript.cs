using Protostar.Registry.Api.Common;

namespace Protostar.Registry.Api.Sessions;

/// <summary>Whether a transcript is a whole session or one subagent's run inside a session.</summary>
public enum TranscriptKind
{
    Session,
    Subagent,
}

/// <summary>
/// One harness transcript file, backed up as an append-only sequence of chunks.
/// </summary>
/// <remarks>
/// <para>
/// Harnesses write transcripts by appending lines, so the registry stores a transcript as chunks that each
/// start where the previous one ended. <see cref="Append"/> enforces that: a chunk must name the current
/// <see cref="Revision"/> and start at <see cref="Length"/>, and must end at a line boundary so every chunk
/// parses on its own. A client that finds its local file was rewritten rather than appended (it shrank, or
/// the bytes it already sent changed) starts a new revision at offset 0; earlier revisions' chunks are kept.
/// </para>
/// <para>
/// The chunks' bytes live in blob storage; this aggregate holds the bookkeeping and a
/// <see cref="TranscriptSummary"/> built from the parsed lines, which is what listings read.
/// </para>
/// </remarks>
public sealed class Transcript : AggregateRoot
{
    /// <summary>The largest chunk accepted, uncompressed.</summary>
    public const int MaxChunkBytes = 16 * 1024 * 1024;

    private Transcript()
    {
    }

    public Guid Id { get; private set; }

    public Guid OwnerId { get; private set; }

    public HarnessId Harness { get; private set; } = default!;

    public TranscriptKey Key { get; private set; } = default!;

    public TranscriptKind Kind { get; private set; }

    /// <summary>For a subagent, the key of the session it ran in; <c>null</c> for a session.</summary>
    public TranscriptKey? ParentKey { get; private set; }

    /// <summary>The machine that registered the transcript.</summary>
    public Guid MachineId { get; private set; }

    /// <summary>Where the file lives on that machine, for display and troubleshooting.</summary>
    public string SourcePath { get; private set; } = default!;

    /// <summary>Harness-specific metadata stored beside the transcript (for a Claude Code subagent, its <c>.meta.json</c>), as JSON.</summary>
    public string? MetadataJson { get; private set; }

    /// <summary>Starts at 1, and increases each time the client starts over because its file was rewritten.</summary>
    public int Revision { get; private set; }

    /// <summary>Bytes stored for the current revision; the offset the next chunk must start at.</summary>
    public long Length { get; private set; }

    public int ChunkCount { get; private set; }

    public TranscriptSummary Summary { get; private set; } = TranscriptSummary.Empty;

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Registers a transcript the registry has not seen.</summary>
    public static Transcript Register(
        Guid ownerId, HarnessId harness, TranscriptKey key, TranscriptKind kind, TranscriptKey? parentKey,
        Guid machineId, string sourcePath, string? metadataJson, DateTimeOffset now)
    {
        if (kind == TranscriptKind.Subagent && parentKey is null)
            throw new ArgumentException("A subagent transcript must name its session.", nameof(parentKey));

        return new Transcript
        {
            Id = Guid.NewGuid(),
            OwnerId = ownerId,
            Harness = harness,
            Key = key,
            Kind = kind,
            ParentKey = kind == TranscriptKind.Subagent ? parentKey : null,
            MachineId = machineId,
            SourcePath = sourcePath,
            MetadataJson = metadataJson,
            Revision = 1,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    /// <summary>Refreshes where the transcript lives and its metadata, for example when it is registered again.</summary>
    public void Describe(Guid machineId, string sourcePath, string? metadataJson, DateTimeOffset now)
    {
        MachineId = machineId;
        SourcePath = sourcePath;
        MetadataJson = metadataJson ?? MetadataJson;
        UpdatedAt = now;
    }

    /// <summary>Whether a chunk at <paramref name="revision"/> and <paramref name="offset"/> would be in position.</summary>
    /// <remarks>
    /// True for the current revision at <see cref="Length"/>, or the next revision at offset 0. Lets a caller
    /// refuse a stale chunk before doing any costly work for it, such as storing its bytes.
    /// </remarks>
    public bool AcceptsChunkAt(int revision, long offset) =>
        (revision == Revision && offset == Length) || (revision == Revision + 1 && offset == 0);

    /// <summary>
    /// Appends a chunk of <paramref name="content"/>, already stored at <paramref name="blobKey"/>, and folds
    /// its parsed lines into the summary.
    /// </summary>
    /// <remarks>
    /// Revision <see cref="Revision"/> + 1 at offset 0 starts a new revision: the length and summary reset and
    /// the chunk becomes its first. Any other revision, or an offset other than <see cref="Length"/>, is
    /// rejected with the current position so the client can resume from it.
    /// </remarks>
    public TranscriptAppendOutcome Append(
        int revision, long offset, ReadOnlySpan<byte> content, string blobKey, int storedBytes,
        Guid machineId, ITranscriptParser parser, DateTimeOffset now)
    {
        if (!AcceptsChunkAt(revision, offset))
            return TranscriptAppendOutcome.Mismatch(Revision, Length);

        var startsNewRevision = revision == Revision + 1;

        if (content.Length == 0)
            return TranscriptAppendOutcome.Rejected("A chunk must not be empty.");
        if (content.Length > MaxChunkBytes)
            return TranscriptAppendOutcome.Rejected($"A chunk may hold at most {MaxChunkBytes} bytes; this one has {content.Length}.");
        if (content[^1] != (byte)'\n')
            return TranscriptAppendOutcome.Rejected("A chunk must end at a line boundary (a newline).");

        if (startsNewRevision)
        {
            Revision = revision;
            Length = 0;
            Summary = TranscriptSummary.Empty;
        }

        var chunk = TranscriptChunk.Create(Id, Revision, Length, content, blobKey, storedBytes, machineId, now);
        Length += content.Length;
        ChunkCount++;
        Summary = Summary.Add(parser.Parse(content));
        UpdatedAt = now;

        return TranscriptAppendOutcome.Appended(chunk);
    }
}

/// <summary>The result of <see cref="Transcript.Append"/>.</summary>
public sealed record TranscriptAppendOutcome
{
    public TranscriptChunk? Chunk { get; private init; }

    /// <summary>Set when the chunk did not start at the current position; carries that position.</summary>
    public (int Revision, long Length)? Expected { get; private init; }

    /// <summary>Why a well-positioned chunk was rejected.</summary>
    public string? Error { get; private init; }

    public static TranscriptAppendOutcome Appended(TranscriptChunk chunk) => new() { Chunk = chunk };

    public static TranscriptAppendOutcome Mismatch(int revision, long length) => new() { Expected = (revision, length) };

    public static TranscriptAppendOutcome Rejected(string error) => new() { Error = error };
}

/// <summary>One stored piece of a transcript: a byte range of one revision, whose bytes are in blob storage.</summary>
public sealed class TranscriptChunk
{
    private TranscriptChunk()
    {
    }

    public Guid Id { get; private set; }

    public Guid TranscriptId { get; private set; }

    public int Revision { get; private set; }

    /// <summary>Where the chunk starts within its revision.</summary>
    public long Offset { get; private set; }

    /// <summary>Uncompressed length.</summary>
    public int Length { get; private set; }

    /// <summary>Lowercase hex SHA-256 of the uncompressed bytes.</summary>
    public string Sha256 { get; private set; } = default!;

    /// <summary>The blob holding the bytes, gzip-compressed.</summary>
    public string BlobKey { get; private set; } = default!;

    /// <summary>Compressed length, as stored.</summary>
    public int StoredBytes { get; private set; }

    /// <summary>The machine that sent the chunk.</summary>
    public Guid MachineId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    internal static TranscriptChunk Create(
        Guid transcriptId, int revision, long offset, ReadOnlySpan<byte> content, string blobKey, int storedBytes,
        Guid machineId, DateTimeOffset now) => new()
        {
            Id = Guid.NewGuid(),
            TranscriptId = transcriptId,
            Revision = revision,
            Offset = offset,
            Length = content.Length,
            Sha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(content)),
            BlobKey = blobKey,
            StoredBytes = storedBytes,
            MachineId = machineId,
            CreatedAt = now,
        };
}
