using System.IO.Compression;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Protostar.Registry.Api.Common;
using Protostar.Registry.Api.Infrastructure;
using Protostar.Registry.Api.Projects;

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
    private readonly TranscriptProjects _projects;
    private readonly IDomainEventDispatcher _events;

    public TranscriptService(RegistryDbContext db, IBlobStore blobs, TranscriptProjects projects, IDomainEventDispatcher events)
    {
        _db = db;
        _blobs = blobs;
        _projects = projects;
        _events = events;
    }

    /// <summary>Registers <paramref name="machineId"/>, or refreshes its details. Returns the machine and whether it was new.</summary>
    /// <remarks>
    /// Safe to call concurrently for the same new id: exactly one caller's insert lands, and every other caller
    /// refreshes the row that insert created, so all of them succeed with that one row. The all-zeros id is
    /// refused as invalid.
    /// </remarks>
    public async Task<TranscriptResult<(Machine Machine, bool Created)>> RegisterMachineAsync(
        Guid ownerId, Guid machineId, string name, string operatingSystem, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var machine = await FindMachineAsync(ownerId, machineId, cancellationToken);
        if (machine is null)
        {
            Machine registered;
            try
            {
                registered = Machine.Register(machineId, ownerId, name, operatingSystem, now);
            }
            catch (MachineIdMissingException ex)
            {
                return TranscriptResult<(Machine, bool)>.Invalid(ex.Message);
            }

            if (await InsertIfAbsentAsync(registered, cancellationToken))
                return TranscriptResult<(Machine, bool)>.Ok((registered, true));

            // Another request registered this id between our read and our insert: refresh its row instead.
            machine = (await FindMachineAsync(ownerId, machineId, cancellationToken))!;
        }

        machine.Update(name, operatingSystem, now);
        await _db.SaveChangesAsync(cancellationToken);
        return TranscriptResult<(Machine, bool)>.Ok((machine, false));
    }

    // Inserts the machine unless its (owner, id) row already exists; true when this call inserted it. ON CONFLICT
    // makes the lost race a no-op instead of a unique violation that EF would log as an error. The column list
    // repeats MachineConfiguration's mapping; a mismatch fails every first registration, not silently.
    private async Task<bool> InsertIfAbsentAsync(Machine machine, CancellationToken cancellationToken) =>
        await _db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO "Machines" ("OwnerId", "Id", "Name", "OperatingSystem", "RegisteredAt", "LastSeenAt")
            VALUES ({machine.OwnerId}, {machine.Id}, {machine.Name}, {machine.OperatingSystem}, {machine.RegisteredAt}, {machine.LastSeenAt})
            ON CONFLICT ("OwnerId", "Id") DO NOTHING
            """,
            cancellationToken) == 1;

    /// <summary>Registers a transcript, or refreshes where it lives, and files it under its project. Returns its current position.</summary>
    /// <remarks>
    /// With <see cref="RegisterTranscriptRequest.Project"/> facts the transcript is filed under the project they
    /// resolve to, replacing any earlier filing. Without them, a transcript whose working directory is already
    /// known from its lines but that was never filed gets a machine-and-folder project.
    /// </remarks>
    public async Task<TranscriptResult<Transcript>> RegisterTranscriptAsync(
        Guid ownerId, string harnessRaw, string keyRaw, RegisterTranscriptRequest request, CancellationToken cancellationToken)
    {
        if (!TryIdentify(harnessRaw, keyRaw, out var harness, out var key, out var invalid))
            return invalid!;

        TranscriptKey? parentKey = null;
        if (request.Kind == TranscriptKind.Subagent && !TranscriptKey.TryCreate(request.ParentKey, out parentKey))
            return TranscriptResult<Transcript>.Invalid("A subagent transcript needs a valid parentKey.");

        if (request.Project?.Problem() is { } badFacts)
            return TranscriptResult<Transcript>.Invalid(badFacts);

        if (!await _db.Machines.AnyAsync(m => m.OwnerId == ownerId && m.Id == request.MachineId, cancellationToken))
            return TranscriptResult<Transcript>.UnknownMachine($"Machine {request.MachineId} is not registered. Register it with PUT /v1/machines/{{machineId}} first.");

        // Resolve first: resolution saves any new project and may reset the change tracker on a race.
        var filing = request.Project is { } facts
            ? await _projects.ResolveAsync(ownerId, request.MachineId, facts, facts.WorkingDirectory, cancellationToken)
            : ((ProjectResolution Resolution, ProjectIdentity Identity)?)null;

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

        if (filing is { } filed)
            transcript.FileUnder(filed.Resolution.ProjectId, filed.Identity, request.Project!.ToJson(), now);

        await _db.SaveChangesAndDispatchAsync(_events, cancellationToken);
        if (filing is null)
            await FileByWorkingDirectoryAsync(transcript, cancellationToken);
        return TranscriptResult<Transcript>.Ok(transcript);
    }

    /// <summary>Appends one chunk to a registered transcript.</summary>
    /// <remarks>
    /// <c>body</c> is the chunk, gzip-compressed when <c>gzip</c> is true. When <c>expectedSha256</c> is given,
    /// the uncompressed chunk must hash to it. Only another append moving the position fails it as a mismatch; an
    /// update that leaves the position alone (a rebuilt summary from <see cref="TranscriptReprocessor"/>, a refiling)
    /// is retried against the updated row, so the chunk is never lost to it.
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

        var machine = await FindMachineAsync(ownerId, machineId, cancellationToken);
        if (machine is null)
            return TranscriptResult<Transcript>.UnknownMachine($"Machine {machineId} is not registered.");

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
        var parser = TranscriptParsers.For(harness);
        var outcome = transcript.Append(revision, offset, raw, blobKey, stored.Length, machineId, parser, now);
        if (outcome.Expected is not null)
            return TranscriptResult<Transcript>.Mismatch(transcript);
        if (outcome.Error is { } error)
            return TranscriptResult<Transcript>.Invalid(error);

        await _blobs.PutAsync(blobKey, stored, cancellationToken);
        for (var attempt = 1; ; attempt++)
        {
            _db.TranscriptChunks.Add(outcome.Chunk!);
            machine.Seen(now);

            try
            {
                await _db.SaveChangesRacingAndDispatchAsync(_events, cancellationToken);
                break;
            }
            catch (Exception ex) when (ex is DbUpdateConcurrencyException || ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                _db.ChangeTracker.Clear();
                var current = (await FindAsync(ownerId, harness, key, cancellationToken))!;

                // The row changed but the chunk is still in position, so no append got in first: another writer (the
                // reprocessor saving a rebuilt summary, a refiling) updated it. Apply the chunk to the row as it is now.
                if (ex is DbUpdateConcurrencyException && attempt < MaxAppendAttempts && current.AcceptsChunkAt(revision, offset) &&
                    await FindMachineAsync(ownerId, machineId, cancellationToken) is { } reloaded)
                {
                    (transcript, machine) = (current, reloaded);
                    outcome = transcript.Append(revision, offset, raw, blobKey, stored.Length, machineId, parser, now);
                    continue;
                }

                // Another append to the same transcript won the race; report where it left things.
                return TranscriptResult<Transcript>.Mismatch(current);
            }
        }

        await FileByWorkingDirectoryAsync(transcript, cancellationToken);
        return TranscriptResult<Transcript>.Ok(transcript);
    }

    // How many times an append is saved when only a non-append update gets in its way. One retry is what a
    // reprocessed summary needs; a few more cover refilings landing at the same moment, without looping forever.
    private const int MaxAppendAttempts = 4;

    // A transcript the client sent no facts for is filed by the working directory its lines recorded, once
    // they have recorded one, as a machine-and-folder project. Runs after the caller's save, because resolving
    // may save a new project of its own.
    private async Task FileByWorkingDirectoryAsync(Transcript transcript, CancellationToken cancellationToken)
    {
        if (transcript.ProjectId is not null || transcript.Summary.WorkingDirectory is not { } directory)
            return;

        var (resolution, identity) = await _projects.ResolveAsync(transcript.OwnerId, transcript.MachineId, null, directory, cancellationToken);
        var tracked = await FindAsync(transcript.OwnerId, transcript.Harness, transcript.Key, cancellationToken);
        tracked!.FileUnder(resolution.ProjectId, identity, null, DateTimeOffset.UtcNow);
        await _db.SaveChangesAndDispatchAsync(_events, cancellationToken);
    }

    /// <summary>The most transcripts one lookup may name.</summary>
    /// <remarks>
    /// 500 keeps the worst case small on every side: a request body of about 120 KB (keys are at most 200
    /// characters), one indexed query with a 500-element array parameter, and an answer of about 60 KB. A large
    /// history (1,666 transcripts on the owner's machine) still verifies in four requests.
    /// </remarks>
    public const int MaxLookup = 500;

    /// <summary>Where each of <paramref name="transcripts"/> ends in the registry, or that it does not hold it, in request order.</summary>
    /// <remarks>
    /// An invalid harness or key cannot name a stored transcript, so it is answered as not known rather than failing
    /// the whole batch. More than <see cref="MaxLookup"/> transcripts is refused.
    /// </remarks>
    public async Task<TranscriptResult<IReadOnlyList<TranscriptLookupResponse>>> LookupAsync(
        Guid ownerId, IReadOnlyList<TranscriptReference> transcripts, CancellationToken cancellationToken)
    {
        if (transcripts.Count > MaxLookup)
            return TranscriptResult<IReadOnlyList<TranscriptLookupResponse>>.Invalid($"Look up at most {MaxLookup} transcripts per request; this one names {transcripts.Count}.");

        var keys = new List<TranscriptKey>();
        foreach (var requested in transcripts)
        {
            if (HarnessId.TryCreate(requested.Harness, out _) && TranscriptKey.TryCreate(requested.Key, out var key))
                keys.Add(key);
        }

        var stored = keys.Count == 0
            ? []
            : await _db.Transcripts.AsNoTracking()
                .Where(t => t.OwnerId == ownerId && keys.Contains(t.Key))
                .Select(t => new { t.Harness, t.Key, t.Revision, t.Length })
                .ToListAsync(cancellationToken);
        var positions = stored.ToDictionary(t => (t.Harness.Value, t.Key.Value), t => (t.Revision, t.Length));

        return TranscriptResult<IReadOnlyList<TranscriptLookupResponse>>.Ok(transcripts
            .Select(t => positions.TryGetValue((t.Harness, t.Key), out var at)
                ? new TranscriptLookupResponse(t.Harness, t.Key, true, at.Revision, at.Length)
                : new TranscriptLookupResponse(t.Harness, t.Key, false, null, null))
            .ToList());
    }

    /// <summary>Lists transcripts, most recently active first.</summary>
    public async Task<IReadOnlyList<TranscriptView>> ListAsync(Guid ownerId, TranscriptQuery query, CancellationToken cancellationToken)
    {
        var transcripts = _db.Transcripts.AsNoTracking().Where(t => t.OwnerId == ownerId && t.Kind == query.Kind);

        if (query.Harness is not null && HarnessId.TryCreate(query.Harness, out var harness))
            transcripts = transcripts.Where(t => t.Harness == harness);
        if (query.MachineId is { } machineId)
            transcripts = transcripts.Where(t => t.MachineId == machineId);
        if (Guid.TryParse(query.Project, out var projectId))
        {
            transcripts = transcripts.Where(t => t.ProjectId == projectId);
        }
        else if (!string.IsNullOrWhiteSpace(query.Project))
        {
            var named = _db.Projects.Where(p => p.OwnerId == ownerId && EF.Functions.ILike(p.Name, "%" + query.Project + "%")).Select(p => p.Id);
            transcripts = transcripts.Where(t => t.ProjectId != null && named.Contains(t.ProjectId.Value));
        }
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

    private Task<Machine?> FindMachineAsync(Guid ownerId, Guid machineId, CancellationToken cancellationToken) =>
        _db.Machines.FirstOrDefaultAsync(m => m.OwnerId == ownerId && m.Id == machineId, cancellationToken);

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

        var projectIds = transcripts.Where(t => t.ProjectId != null).Select(t => t.ProjectId!.Value).Distinct().ToList();
        var projectNames = await _db.Projects.AsNoTracking()
            .Where(p => p.OwnerId == ownerId && projectIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => p.Name, cancellationToken);

        return transcripts.Select(t => new TranscriptView(
            t,
            machines.GetValueOrDefault(t.MachineId),
            t.ProjectId is { } project ? projectNames.GetValueOrDefault(project) : null,
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
/// <param name="Project">What the client found out about the session's working directory, to file it under its project; optional.</param>
public sealed record RegisterTranscriptRequest(
    TranscriptKind Kind, string? ParentKey, Guid MachineId, string SourcePath, System.Text.Json.JsonElement? Metadata, ProjectFacts? Project = null);

/// <summary>Filters for listing transcripts.</summary>
public sealed record TranscriptQuery(
    TranscriptKind Kind, string? Harness, Guid? MachineId, string? Project, DateTimeOffset? Since, DateTimeOffset? Before, int Limit);

/// <summary>A transcript joined with what listings show beside it.</summary>
/// <param name="Transcript">The transcript.</param>
/// <param name="Machine">The machine it came from, if still registered.</param>
/// <param name="ProjectName">Its project's display name, or <c>null</c> while it is not filed under one.</param>
/// <param name="SubagentCount">Subagent transcripts of a session.</param>
public sealed record TranscriptView(Transcript Transcript, Machine? Machine, string? ProjectName, int SubagentCount);

/// <summary>Why a transcript operation did not succeed.</summary>
public enum TranscriptFailure
{
    None,
    Invalid,

    /// <summary>The request named a machine the caller has not registered.</summary>
    UnknownMachine,

    /// <summary>No transcript with that harness and key.</summary>
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
    public static TranscriptResult<T> UnknownMachine(string message) => new(TranscriptFailure.UnknownMachine, default, message, null);
    public static TranscriptResult<T> NotFound() => new(TranscriptFailure.NotFound, default, "No transcript with that harness and key.", null);
    public static TranscriptResult<T> Conflict(string message) => new(TranscriptFailure.Conflict, default, message, null);
    public static TranscriptResult<T> TooLarge(string message) => new(TranscriptFailure.TooLarge, default, message, null);

    /// <summary>The chunk did not start at the transcript's current position, carried in <see cref="Current"/>.</summary>
    public static TranscriptResult<T> Mismatch(Transcript current) =>
        new(TranscriptFailure.Mismatch, default, "The chunk does not start at the transcript's current position.", current);
}
