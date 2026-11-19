using System.IO.Compression;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Protostar.Registry.Api.Infrastructure;

namespace Protostar.Registry.Api.Sessions;

/// <summary>Application service for backing up and reading machines' transcripts.</summary>
/// <remarks>
/// Owns lookups, transactions and blob I/O; the append rules live on <see cref="Transcript"/>. Every
/// operation is scoped to <c>ownerId</c>, the authenticated caller, so one user can never reach another's
/// machines or transcripts.
/// </remarks>
public sealed class TranscriptService
{
    private readonly RegistryDbContext _db;
    private readonly IBlobStore _blobs;

    public TranscriptService(RegistryDbContext db, IBlobStore blobs)
    {
        _db = db;
        _blobs = blobs;
    }

    /// <summary>Registers <paramref name="machineId"/>, or refreshes its details. Returns the machine and whether it was new.</summary>
    public async Task<(Machine Machine, bool Created)> RegisterMachineAsync(
        Guid ownerId, Guid machineId, string name, string operatingSystem, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var machine = await _db.Machines.FirstOrDefaultAsync(m => m.OwnerId == ownerId && m.Id == machineId, cancellationToken);
        var created = machine is null;
        if (machine is null)
        {
            machine = Machine.Register(machineId, ownerId, name, operatingSystem, now);
            _db.Machines.Add(machine);
        }
        else
        {
            machine.Update(name, operatingSystem, now);
        }

        await _db.SaveChangesAsync(cancellationToken);
        return (machine, created);
    }

    /// <summary>Registers a transcript, or refreshes where it lives. Returns its current position.</summary>
    public async Task<TranscriptResult<Transcript>> RegisterTranscriptAsync(
        Guid ownerId, string harnessRaw, string keyRaw, RegisterTranscriptRequest request, CancellationToken cancellationToken)
    {
        if (!TryIdentify(harnessRaw, keyRaw, out var harness, out var key, out var invalid))
            return invalid!;

        TranscriptKey? parentKey = null;
        if (request.Kind == TranscriptKind.Subagent && !TranscriptKey.TryCreate(request.ParentKey, out parentKey))
            return TranscriptResult<Transcript>.Invalid("A subagent transcript needs a valid parentKey.");

        if (!await _db.Machines.AnyAsync(m => m.OwnerId == ownerId && m.Id == request.MachineId, cancellationToken))
            return TranscriptResult<Transcript>.Invalid($"Machine {request.MachineId} is not registered. Register it with PUT /v1/machines/{{machineId}} first.");

        var now = DateTimeOffset.UtcNow;
        var transcript = await FindAsync(ownerId, harness, key, cancellationToken);
        if (transcript is null)
        {
            transcript = Transcript.Register(ownerId, harness, key, request.Kind, parentKey, request.MachineId, request.SourcePath, request.Metadata?.GetRawText(), now);
            _db.Transcripts.Add(transcript);
        }
        else if (transcript.Kind != request.Kind)
        {
            return TranscriptResult<Transcript>.Conflict($"This transcript is already registered as a {transcript.Kind.ToString().ToLowerInvariant()}.");
        }
        else
        {
            transcript.Describe(request.MachineId, request.SourcePath, request.Metadata?.GetRawText(), now);
        }

        await _db.SaveChangesAsync(cancellationToken);
        return TranscriptResult<Transcript>.Ok(transcript);
    }

    /// <summary>Appends one chunk to a registered transcript.</summary>
    /// <remarks>
    /// <c>body</c> is the chunk, gzip-compressed when <c>gzip</c> is true. When <c>expectedSha256</c> is given,
    /// the uncompressed chunk must hash to it.
    /// </remarks>
    public async Task<TranscriptResult<Transcript>> AppendAsync(
        Guid ownerId, string harnessRaw, string keyRaw, int revision, long offset, Guid machineId,
        Stream body, bool gzip, string? expectedSha256, CancellationToken cancellationToken)
    {
        if (!TryIdentify(harnessRaw, keyRaw, out var harness, out var key, out var invalid))
            return invalid!;

        var transcript = await FindAsync(ownerId, harness, key, cancellationToken);
        if (transcript is null)
            return TranscriptResult<Transcript>.NotFound();

        var machine = await _db.Machines.FirstOrDefaultAsync(m => m.OwnerId == ownerId && m.Id == machineId, cancellationToken);
        if (machine is null)
            return TranscriptResult<Transcript>.Invalid($"Machine {machineId} is not registered.");

        // Check the position before storing anything, so a stale client leaves no orphaned blob behind.
        if (!transcript.AcceptsChunkAt(revision, offset))
            return TranscriptResult<Transcript>.Mismatch(transcript);

        byte[] raw;
        byte[] stored;
        try
        {
            (raw, stored) = await ReadChunkAsync(body, gzip, cancellationToken);
        }
        catch (InvalidDataException ex)
        {
            return TranscriptResult<Transcript>.Invalid(ex.Message);
        }
        catch (ChunkTooLargeException)
        {
            return TranscriptResult<Transcript>.TooLarge($"A chunk may hold at most {Transcript.MaxChunkBytes} bytes uncompressed.");
        }

        if (expectedSha256 is not null &&
            !string.Equals(Convert.ToHexStringLower(SHA256.HashData(raw)), expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            return TranscriptResult<Transcript>.Invalid("The chunk does not match its X-Content-SHA256 header; it was corrupted in transit.");
        }

        var blobKey = $"{TranscriptChunk.BlobKeyPrefix}{ownerId:N}/{transcript.Id:N}/r{revision}/{offset:D16}-{Guid.NewGuid():N}.jsonl.gz";
        var now = DateTimeOffset.UtcNow;
        var outcome = transcript.Append(revision, offset, raw, blobKey, stored.Length, machineId, TranscriptParsers.For(harness), now);
        if (outcome.Expected is not null)
            return TranscriptResult<Transcript>.Mismatch(transcript);
        if (outcome.Error is { } error)
            return TranscriptResult<Transcript>.Invalid(error);

        await _blobs.PutAsync(blobKey, stored, cancellationToken);
        _db.TranscriptChunks.Add(outcome.Chunk!);
        machine.Seen(now);

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is DbUpdateConcurrencyException || ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Another append to the same transcript won the race; report where it left things.
            _db.ChangeTracker.Clear();
            var current = await FindAsync(ownerId, harness, key, cancellationToken);
            return TranscriptResult<Transcript>.Mismatch(current!);
        }

        return TranscriptResult<Transcript>.Ok(transcript);
    }

    /// <summary>Lists transcripts, most recently active first.</summary>
    public async Task<IReadOnlyList<TranscriptView>> ListAsync(Guid ownerId, TranscriptQuery query, CancellationToken cancellationToken)
    {
        var transcripts = _db.Transcripts.AsNoTracking().Where(t => t.OwnerId == ownerId && t.Kind == query.Kind);

        if (query.Harness is not null && HarnessId.TryCreate(query.Harness, out var harness))
            transcripts = transcripts.Where(t => t.Harness == harness);
        if (query.MachineId is { } machineId)
            transcripts = transcripts.Where(t => t.MachineId == machineId);
        if (!string.IsNullOrWhiteSpace(query.Project))
            transcripts = transcripts.Where(t => EF.Functions.ILike(t.Summary.WorkingDirectory!, "%" + query.Project + "%"));
        if (query.Since is { } since)
            transcripts = transcripts.Where(t => t.Summary.LastActivityAt >= since);
        if (query.Before is { } before)
            transcripts = transcripts.Where(t => t.Summary.LastActivityAt < before);

        var page = await transcripts
            .OrderByDescending(t => t.Summary.LastActivityAt)
            .ThenByDescending(t => t.CreatedAt)
            .Take(Math.Clamp(query.Limit, 1, 200))
            .ToListAsync(cancellationToken);

        return await ToViewsAsync(ownerId, page, cancellationToken);
    }

    /// <summary>One transcript, with its subagents when it is a session.</summary>
    public async Task<TranscriptResult<(TranscriptView View, IReadOnlyList<TranscriptView> Subagents)>> GetAsync(
        Guid ownerId, string harnessRaw, string keyRaw, CancellationToken cancellationToken)
    {
        if (!HarnessId.TryCreate(harnessRaw, out var harness) || !TranscriptKey.TryCreate(keyRaw, out var key))
            return TranscriptResult<(TranscriptView, IReadOnlyList<TranscriptView>)>.NotFound();

        var transcript = await _db.Transcripts.AsNoTracking()
            .FirstOrDefaultAsync(t => t.OwnerId == ownerId && t.Harness == harness && t.Key == key, cancellationToken);
        if (transcript is null)
            return TranscriptResult<(TranscriptView, IReadOnlyList<TranscriptView>)>.NotFound();

        var subagents = transcript.Kind == TranscriptKind.Session
            ? await _db.Transcripts.AsNoTracking()
                .Where(t => t.OwnerId == ownerId && t.Harness == harness && t.ParentKey == key)
                .OrderBy(t => t.Summary.StartedAt)
                .ToListAsync(cancellationToken)
            : [];

        var views = await ToViewsAsync(ownerId, [transcript, .. subagents], cancellationToken);
        return TranscriptResult<(TranscriptView, IReadOnlyList<TranscriptView>)>.Ok((views[0], views.Skip(1).ToList()));
    }

    /// <summary>Finds a transcript and the chunks of its current revision, in order, for download.</summary>
    public async Task<(Transcript Transcript, IReadOnlyList<TranscriptChunk> Chunks)?> OpenAsync(
        Guid ownerId, string harnessRaw, string keyRaw, CancellationToken cancellationToken)
    {
        if (!HarnessId.TryCreate(harnessRaw, out var harness) || !TranscriptKey.TryCreate(keyRaw, out var key))
            return null;

        var transcript = await _db.Transcripts.AsNoTracking()
            .FirstOrDefaultAsync(t => t.OwnerId == ownerId && t.Harness == harness && t.Key == key, cancellationToken);
        if (transcript is null)
            return null;

        var chunks = await _db.TranscriptChunks.AsNoTracking()
            .Where(c => c.TranscriptId == transcript.Id && c.Revision == transcript.Revision)
            .OrderBy(c => c.Offset)
            .ToListAsync(cancellationToken);
        return (transcript, chunks);
    }

    /// <summary>Writes the uncompressed bytes of <paramref name="chunks"/>, in order, to <paramref name="destination"/>.</summary>
    public async Task CopyContentAsync(IReadOnlyList<TranscriptChunk> chunks, Stream destination, CancellationToken cancellationToken)
    {
        foreach (var chunk in chunks)
        {
            using var compressed = new MemoryStream(chunk.StoredBytes);
            await _blobs.CopyToAsync(chunk.BlobKey, compressed, cancellationToken);
            compressed.Position = 0;
            await using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
            await gzip.CopyToAsync(destination, cancellationToken);
        }
    }

    private Task<Transcript?> FindAsync(Guid ownerId, HarnessId harness, TranscriptKey key, CancellationToken cancellationToken) =>
        _db.Transcripts.FirstOrDefaultAsync(t => t.OwnerId == ownerId && t.Harness == harness && t.Key == key, cancellationToken);

    private async Task<IReadOnlyList<TranscriptView>> ToViewsAsync(Guid ownerId, IReadOnlyList<Transcript> transcripts, CancellationToken cancellationToken)
    {
        var machineIds = transcripts.Select(t => t.MachineId).Distinct().ToList();
        var machines = await _db.Machines.AsNoTracking()
            .Where(m => m.OwnerId == ownerId && machineIds.Contains(m.Id))
            .ToDictionaryAsync(m => m.Id, cancellationToken);

        var sessionKeys = transcripts.Where(t => t.Kind == TranscriptKind.Session).Select(t => t.Key).ToList();
        var subagentCounts = await _db.Transcripts.AsNoTracking()
            .Where(t => t.OwnerId == ownerId && t.ParentKey != null && sessionKeys.Contains(t.ParentKey))
            .GroupBy(t => new { t.Harness, t.ParentKey })
            .Select(g => new { g.Key.Harness, g.Key.ParentKey, Count = g.Count() })
            .ToListAsync(cancellationToken);

        return transcripts.Select(t => new TranscriptView(
            t,
            machines.GetValueOrDefault(t.MachineId),
            subagentCounts.FirstOrDefault(c => c.Harness == t.Harness && c.ParentKey == t.Key)?.Count ?? 0)).ToList();
    }

    // Reads the body into memory, enforcing the chunk cap on the uncompressed size (so a small gzip body cannot
    // expand without limit), and returns the raw bytes plus the gzip bytes to store.
    private static async Task<(byte[] Raw, byte[] Stored)> ReadChunkAsync(Stream body, bool gzip, CancellationToken cancellationToken)
    {
        using var received = new MemoryStream();
        await CopyLimitedAsync(body, received, Transcript.MaxChunkBytes, cancellationToken);
        if (!gzip)
        {
            var raw = received.ToArray();
            return (raw, Compress(raw));
        }

        received.Position = 0;
        using var decompressed = new MemoryStream();
        await using (var unzip = new GZipStream(received, CompressionMode.Decompress, leaveOpen: true))
            await CopyLimitedAsync(unzip, decompressed, Transcript.MaxChunkBytes, cancellationToken);
        return (decompressed.ToArray(), received.ToArray());
    }

    private static async Task CopyLimitedAsync(Stream source, Stream destination, int limit, CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            total += read;
            if (total > limit)
                throw new ChunkTooLargeException();
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }

    private static byte[] Compress(byte[] raw)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
            gzip.Write(raw);
        return output.ToArray();
    }

    private static bool TryIdentify(string harnessRaw, string keyRaw, out HarnessId harness, out TranscriptKey key, out TranscriptResult<Transcript>? invalid)
    {
        invalid = null;
        key = null!;
        if (!HarnessId.TryCreate(harnessRaw, out harness))
            invalid = TranscriptResult<Transcript>.Invalid("The harness id must be lowercase letters, digits and dashes.");
        else if (!TranscriptKey.TryCreate(keyRaw, out key))
            invalid = TranscriptResult<Transcript>.Invalid("The transcript key must be 1 to 200 letters, digits, '.', '_' or '-'.");
        return invalid is null;
    }

    private sealed class ChunkTooLargeException : Exception;
}

/// <summary>What to register a transcript as.</summary>
/// <param name="Kind">Session or subagent.</param>
/// <param name="ParentKey">For a subagent, the key of the session it ran in.</param>
/// <param name="MachineId">The registered machine the file lives on.</param>
/// <param name="SourcePath">The file's path on that machine.</param>
/// <param name="Metadata">Harness-specific metadata kept beside the transcript, such as a Claude Code subagent's <c>.meta.json</c>.</param>
public sealed record RegisterTranscriptRequest(
    TranscriptKind Kind, string? ParentKey, Guid MachineId, string SourcePath, System.Text.Json.JsonElement? Metadata);

/// <summary>Filters for listing transcripts.</summary>
public sealed record TranscriptQuery(
    TranscriptKind Kind, string? Harness, Guid? MachineId, string? Project, DateTimeOffset? Since, DateTimeOffset? Before, int Limit);

/// <summary>A transcript joined with what listings show beside it.</summary>
public sealed record TranscriptView(Transcript Transcript, Machine? Machine, int SubagentCount);

/// <summary>Why a transcript operation did not succeed.</summary>
public enum TranscriptFailure
{
    None,
    Invalid,
    NotFound,
    Conflict,
    Mismatch,
    TooLarge,
}

/// <summary>A transcript operation's result: a value, or a typed failure the endpoint maps to a status code.</summary>
public sealed record TranscriptResult<T>(TranscriptFailure Failure, T? Value, string? Message, Transcript? Current)
{
    public static TranscriptResult<T> Ok(T value) => new(TranscriptFailure.None, value, null, null);
    public static TranscriptResult<T> Invalid(string message) => new(TranscriptFailure.Invalid, default, message, null);
    public static TranscriptResult<T> NotFound() => new(TranscriptFailure.NotFound, default, "No transcript with that harness and key.", null);
    public static TranscriptResult<T> Conflict(string message) => new(TranscriptFailure.Conflict, default, message, null);
    public static TranscriptResult<T> TooLarge(string message) => new(TranscriptFailure.TooLarge, default, message, null);

    /// <summary>The chunk did not start at the transcript's current position, carried in <see cref="Current"/>.</summary>
    public static TranscriptResult<T> Mismatch(Transcript current) =>
        new(TranscriptFailure.Mismatch, default, "The chunk does not start at the transcript's current position.", current);
}
