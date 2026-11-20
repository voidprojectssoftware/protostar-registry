using System.IO.Compression;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Protostar.Registry.Api.Infrastructure;
using Protostar.Registry.Api.Search;

namespace Protostar.Registry.Api.Sessions;

/// <summary>One pass that re-reads stored transcripts whose projections an older registry made: their summary, their search entries, or both.</summary>
/// <remarks>
/// <para>
/// A transcript is due when its summary is below <see cref="TranscriptSummary.CurrentParserVersion"/>, or when a chunk
/// of its current revision has no search entry of <see cref="SearchEntry.CurrentIndexVersion"/> (from before the index,
/// an older index version, or indexing that failed as it arrived). Its current revision's chunks are read back from the
/// object store in order, one chunk in memory at a time (at most <see cref="Transcript.MaxChunkBytes"/>), so a
/// transcript of any size costs one chunk of memory. Only the chunks the work needs are read: all of them to rebuild a
/// summary, otherwise just the ones missing entries.
/// </para>
/// <para>
/// Nothing disappears: the old summary stays until the rebuilt one replaces it in a single update, so a listing during
/// a pass shows every session. An append that lands while a summary is being rebuilt is never lost. The rebuilt summary
/// covers a known revision and length, and is saved against the row version it was checked with: if an append commits
/// first, the save fails, the chunks that arrived are folded in (as the append itself would have), and the save is tried
/// again; if the save commits first, the append's own save fails on the row version and
/// <see cref="TranscriptService.AppendAsync"/> applies the chunk to the rebuilt row. A transcript that started a new
/// revision meanwhile needs nothing: its summary was started fresh by this registry.
/// </para>
/// <para>
/// Every read and write is keyed by the transcript's id, and its entries are written under its owner, so a pass never
/// mixes owners. Logs carry ids and counts, never content. A transcript that fails (a blob is gone, say) is logged,
/// skipped for the rest of the pass, and tried again on the next one. Safe to run on several instances at once: the
/// summary save is guarded by the row version, and duplicate search entries fail on their unique index.
/// </para>
/// </remarks>
public sealed class TranscriptReprocessor
{
    private readonly RegistryDbContext _db;
    private readonly IBlobStore _blobs;
    private readonly SearchIndexer _indexer;
    private readonly ReprocessingOptions _options;
    private readonly ILogger<TranscriptReprocessor> _logger;

    public TranscriptReprocessor(
        RegistryDbContext db, IBlobStore blobs, SearchIndexer indexer, IOptions<ReprocessingOptions> options, ILogger<TranscriptReprocessor> logger)
    {
        _db = db;
        _blobs = blobs;
        _indexer = indexer;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>Runs one pass over every due transcript and returns what it did.</summary>
    /// <remarks>
    /// Works through due transcripts most recently active first, <see cref="ReprocessingOptions.BatchSize"/> at a time,
    /// pausing <see cref="ReprocessingOptions.BatchPause"/> after each full batch.
    /// </remarks>
    public async Task<TranscriptReprocessResult> RunAsync(CancellationToken cancellationToken)
    {
        var failed = new HashSet<Guid>();
        var result = new TranscriptReprocessResult(0, 0, 0);
        while (true)
        {
            var batch = await _db.Transcripts.AsNoTracking()
                .Where(t => !failed.Contains(t.Id) &&
                    (t.Summary.ParserVersion < TranscriptSummary.CurrentParserVersion ||
                     _db.TranscriptChunks.Any(c => c.TranscriptId == t.Id && c.Revision == t.Revision &&
                        !_db.SearchEntries.Any(e => e.ChunkId == c.Id && e.IndexVersion == SearchEntry.CurrentIndexVersion))))
                .OrderByDescending(t => t.Summary.LastActivityAt)
                .ThenBy(t => t.Id)
                .Select(t => t.Id)
                .Take(_options.BatchSize)
                .ToListAsync(cancellationToken);

            foreach (var id in batch)
            {
                try
                {
                    result += await ReprocessAsync(id, cancellationToken);
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                {
                    failed.Add(id);
                    result += new TranscriptReprocessResult(0, 0, 1);
                    _logger.LogWarning(ex, "Reprocessing could not finish transcript {TranscriptId}; it will be retried on the next pass", id);
                }
            }

            // A short batch was everything still due.
            if (batch.Count < _options.BatchSize)
                break;
            await Task.Delay(_options.BatchPause, cancellationToken);
        }

        _logger.LogInformation(
            "Reprocessing rebuilt {Summaries} transcript summaries and indexed {Chunks} chunks; {Failed} transcripts failed",
            result.Summaries, result.Chunks, result.Failed);
        return result;
    }

    private async Task<TranscriptReprocessResult> ReprocessAsync(Guid transcriptId, CancellationToken cancellationToken)
    {
        _db.ChangeTracker.Clear();
        var transcript = await _db.Transcripts.AsNoTracking().SingleOrDefaultAsync(t => t.Id == transcriptId, cancellationToken);
        if (transcript is null)
            return new(0, 0, 0);

        var parser = TranscriptParsers.For(transcript.Harness);
        var rebuild = !transcript.Summary.IsCurrent;
        var (revision, length) = (transcript.Revision, transcript.Length);

        // Only what the transcript held when it was read: a chunk appended since is handled when the summary is saved.
        var chunks = await _db.TranscriptChunks.AsNoTracking()
            .Where(c => c.TranscriptId == transcriptId && c.Revision == revision && c.Offset < length)
            .OrderBy(c => c.Offset)
            .Select(c => new
            {
                Chunk = c,
                Indexed = _db.SearchEntries.Any(e => e.ChunkId == c.Id && e.IndexVersion == SearchEntry.CurrentIndexVersion),
            })
            .ToListAsync(cancellationToken);

        var summary = TranscriptSummary.Empty;
        var indexed = 0;
        foreach (var item in chunks)
        {
            if (!rebuild && item.Indexed)
                continue;

            var facts = parser.Parse(await ReadChunkAsync(item.Chunk, cancellationToken));
            if (rebuild)
                summary = summary.Add(facts);
            if (!item.Indexed)
            {
                await _indexer.IndexChunkAsync(transcript.OwnerId, transcriptId, item.Chunk.Id, item.Chunk.MachineId, false, facts.Text, cancellationToken);
                indexed++;
            }
        }

        if (!rebuild)
            return new(0, indexed, 0);

        var saved = await SaveSummaryAsync(transcriptId, parser, summary, revision, length, cancellationToken);
        return new(saved ? 1 : 0, indexed, 0);
    }

    // Saves the rebuilt summary against the row as loaded here. Each conflict means another writer committed in between;
    // reloading shows whether that was an append (fold in its chunks and retry), a new revision (nothing to do), or a
    // change to other columns (retry as is). Every retry reads only what arrived since the last, so it converges.
    private async Task<bool> SaveSummaryAsync(
        Guid transcriptId, ITranscriptParser parser, TranscriptSummary summary, int revision, long length, CancellationToken cancellationToken)
    {
        while (true)
        {
            _db.ChangeTracker.Clear();
            var transcript = await _db.Transcripts.SingleOrDefaultAsync(t => t.Id == transcriptId, cancellationToken);
            if (transcript is null || transcript.Revision != revision)
                return false;

            if (transcript.Length > length)
            {
                var arrived = await _db.TranscriptChunks.AsNoTracking()
                    .Where(c => c.TranscriptId == transcriptId && c.Revision == revision && c.Offset >= length && c.Offset < transcript.Length)
                    .OrderBy(c => c.Offset)
                    .ToListAsync(cancellationToken);
                foreach (var chunk in arrived)
                {
                    summary = summary.Add(parser.Parse(await ReadChunkAsync(chunk, cancellationToken)));
                    length = chunk.Offset + chunk.Length;
                }
            }

            if (!transcript.ReplaceSummary(summary, revision, length))
                continue;

            try
            {
                await _db.SaveChangesRacingAsync(cancellationToken);
                return true;
            }
            catch (DbUpdateConcurrencyException)
            {
                // Someone else wrote the row since it was loaded above; go round again.
            }
        }
    }

    private async Task<byte[]> ReadChunkAsync(TranscriptChunk chunk, CancellationToken cancellationToken)
    {
        using var compressed = new MemoryStream(chunk.StoredBytes);
        await _blobs.CopyToAsync(chunk.BlobKey, compressed, cancellationToken);
        compressed.Position = 0;
        using var raw = new MemoryStream(chunk.Length);
        await using (var gzip = new GZipStream(compressed, CompressionMode.Decompress))
            await gzip.CopyToAsync(raw, cancellationToken);
        return raw.ToArray();
    }
}

/// <summary>What one reprocessing pass over transcripts did.</summary>
/// <param name="Summaries">Transcript summaries rebuilt and saved.</param>
/// <param name="Chunks">Transcript chunks (re)indexed for search.</param>
/// <param name="Failed">Transcripts that failed and were left for the next pass.</param>
public sealed record TranscriptReprocessResult(int Summaries, int Chunks, int Failed)
{
    public static TranscriptReprocessResult operator +(TranscriptReprocessResult a, TranscriptReprocessResult b) =>
        new(a.Summaries + b.Summaries, a.Chunks + b.Chunks, a.Failed + b.Failed);
}

/// <summary>How the background reprocessing pass runs (configuration section <c>Reprocessing</c>).</summary>
public sealed class ReprocessingOptions
{
    public const string Section = "Reprocessing";

    /// <summary>Whether this instance runs the pass in the background. Default true.</summary>
    /// <remarks>The <c>reparse-sessions</c> command runs a pass whatever this says.</remarks>
    public bool Enabled { get; set; } = true;

    /// <summary>The time between background passes. Default one hour.</summary>
    /// <remarks>
    /// A pass runs at startup, which is when a version bump arrives, so the interval only decides how soon content whose
    /// processing failed is retried. A pass with nothing to do is a few cheap queries.
    /// </remarks>
    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Transcripts taken per batch. Default 20.</summary>
    /// <remarks>
    /// Memory does not depend on it (one chunk is read at a time whatever the batch), so it sets how much work runs
    /// between pauses and how often the database is asked what is still due. 20 is the batch the search backfill used.
    /// </remarks>
    public int BatchSize { get; set; } = 20;

    /// <summary>The pause after each full batch. Default two seconds.</summary>
    /// <remarks>
    /// Reparsing a history is bulk work sharing the database and object store with live appends and reads. Twenty
    /// typical transcripts (a few MiB each) take a few seconds to read and parse, so a two-second pause keeps the pass
    /// from holding either store continuously, while a history of a couple of thousand transcripts still finishes in
    /// minutes. A pause only follows a full batch, so a pass with little to do never waits.
    /// </remarks>
    public TimeSpan BatchPause { get; set; } = TimeSpan.FromSeconds(2);
}

/// <summary>Runs the reprocessing pass at startup and then every <see cref="ReprocessingOptions.Interval"/>.</summary>
/// <remarks>
/// One pass is <see cref="TranscriptReprocessor"/> (summaries and transcript search entries), then
/// <see cref="SearchBackfill"/> (memory files, and entries of superseded revisions). A failed pass is logged and the next
/// runs on schedule.
/// </remarks>
public sealed class ReprocessingService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly TimeSpan _interval;
    private readonly ILogger<ReprocessingService> _logger;

    public ReprocessingService(IServiceProvider services, IOptions<ReprocessingOptions> options, ILogger<ReprocessingService> logger)
    {
        _services = services;
        _interval = options.Value.Interval;
        _logger = logger;
    }

    /// <summary>Runs one whole pass in a scope of its own: what the background service runs, and the <c>reparse-sessions</c> command.</summary>
    public static async Task<(TranscriptReprocessResult Transcripts, SearchBackfillResult Search)> RunPassAsync(
        IServiceProvider services, CancellationToken cancellationToken)
    {
        await using var scope = services.CreateAsyncScope();
        var transcripts = await scope.ServiceProvider.GetRequiredService<TranscriptReprocessor>().RunAsync(cancellationToken);
        var search = await scope.ServiceProvider.GetRequiredService<SearchBackfill>().RunAsync(cancellationToken);
        return (transcripts, search);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_interval);
        do
        {
            try
            {
                await RunPassAsync(_services, stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogError(ex, "Reprocessing failed; retrying in {Interval}", _interval);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
