using System.IO.Compression;
using Microsoft.EntityFrameworkCore;
using Protostar.Registry.Api.Infrastructure;
using Protostar.Registry.Api.Sessions;

namespace Protostar.Registry.Api.Search;

/// <summary>One pass that indexes whatever the search index is missing: content from before it existed, from an older index version, or whose indexing failed.</summary>
/// <remarks>
/// <para>
/// Missing means: a chunk of a transcript's current revision with no entry of <see cref="SearchEntry.CurrentIndexVersion"/>,
/// or a memory file with no such entry made from its latest change. Entries left from a transcript's earlier
/// revisions are removed. Work goes in small batches, one chunk's bytes in memory at a time.
/// </para>
/// <para>
/// A chunk or file that fails (its blob is gone, say) is logged by id, skipped for the rest of the pass, and tried
/// again on the next one. Safe to run on several instances at once: entries are unique per source and segment, so a
/// duplicate write fails and is skipped.
/// </para>
/// </remarks>
public sealed class SearchBackfill
{
    private const int BatchSize = 20;

    private readonly RegistryDbContext _db;
    private readonly IBlobStore _blobs;
    private readonly SearchIndexer _indexer;
    private readonly ILogger<SearchBackfill> _logger;

    public SearchBackfill(RegistryDbContext db, IBlobStore blobs, SearchIndexer indexer, ILogger<SearchBackfill> logger)
    {
        _db = db;
        _blobs = blobs;
        _indexer = indexer;
        _logger = logger;
    }

    /// <summary>Runs one pass over everything and returns what it indexed.</summary>
    public async Task<SearchBackfillResult> RunAsync(CancellationToken cancellationToken)
    {
        var superseded = await _db.SearchEntries
            .Where(e => e.ChunkId != null && _db.TranscriptChunks.Any(c => c.Id == e.ChunkId &&
                _db.Transcripts.Any(t => t.Id == c.TranscriptId && t.Revision != c.Revision)))
            .ExecuteDeleteAsync(cancellationToken);

        var chunks = await IndexChunksAsync(cancellationToken);
        var files = await IndexMemoryFilesAsync(cancellationToken);
        _logger.LogInformation(
            "Search backfill indexed {Chunks} transcript chunks and {Files} memory files, and removed {Superseded} entries of earlier revisions",
            chunks, files, superseded);
        return new SearchBackfillResult(chunks, files, superseded);
    }

    private async Task<int> IndexChunksAsync(CancellationToken cancellationToken)
    {
        var failed = new HashSet<Guid>();
        var indexed = 0;
        while (true)
        {
            var batch = await _db.TranscriptChunks.AsNoTracking()
                .Join(_db.Transcripts.AsNoTracking(), c => c.TranscriptId, t => t.Id, (c, t) => new { Chunk = c, t.OwnerId, t.Harness, t.Revision })
                .Where(x => x.Chunk.Revision == x.Revision && !failed.Contains(x.Chunk.Id) &&
                    !_db.SearchEntries.Any(e => e.ChunkId == x.Chunk.Id && e.IndexVersion == SearchEntry.CurrentIndexVersion))
                .OrderBy(x => x.Chunk.CreatedAt)
                .Take(BatchSize)
                .ToListAsync(cancellationToken);
            if (batch.Count == 0)
                return indexed;

            foreach (var item in batch)
            {
                try
                {
                    var raw = await ReadChunkAsync(item.Chunk, cancellationToken);
                    var text = TranscriptParsers.For(item.Harness).Parse(raw).Text;
                    await _indexer.IndexChunkAsync(item.OwnerId, item.Chunk.TranscriptId, item.Chunk.Id, item.Chunk.MachineId, false, text, cancellationToken);
                    indexed++;
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                {
                    failed.Add(item.Chunk.Id);
                    _logger.LogWarning(ex, "Search backfill could not index chunk {ChunkId}; it will be retried on the next pass", item.Chunk.Id);
                }
            }
        }
    }

    private async Task<int> IndexMemoryFilesAsync(CancellationToken cancellationToken)
    {
        var failed = new HashSet<Guid>();
        var indexed = 0;
        while (true)
        {
            var batch = await _db.MemoryFiles.AsNoTracking()
                .Where(f => !failed.Contains(f.Id) &&
                    !_db.SearchEntries.Any(e => e.MemoryFileId == f.Id && e.IndexVersion == SearchEntry.CurrentIndexVersion && e.MemoryFileUpdatedAt == f.UpdatedAt))
                .OrderBy(f => f.CreatedAt)
                .Select(f => f.Id)
                .Take(BatchSize)
                .ToListAsync(cancellationToken);
            if (batch.Count == 0)
                return indexed;

            foreach (var id in batch)
            {
                try
                {
                    await _indexer.IndexMemoryFileAsync(id, cancellationToken);
                    indexed++;
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                {
                    failed.Add(id);
                    _logger.LogWarning(ex, "Search backfill could not index memory file {MemoryFileId}; it will be retried on the next pass", id);
                }
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

/// <summary>What one backfill pass did.</summary>
/// <param name="Chunks">Transcript chunks indexed.</param>
/// <param name="MemoryFiles">Memory files indexed.</param>
/// <param name="Superseded">Entries removed because their chunk belongs to an earlier revision.</param>
public sealed record SearchBackfillResult(int Chunks, int MemoryFiles, int Superseded);

/// <summary>Runs <see cref="SearchBackfill"/> at startup and then every <see cref="Interval"/>.</summary>
/// <remarks>
/// At startup it indexes what was uploaded before the index existed, or re-indexes after a version bump; later passes
/// pick up anything whose indexing failed as it arrived. A pass with nothing to do is two cheap queries. A failed pass
/// is logged and the next runs on schedule.
/// </remarks>
public sealed class SearchBackfillService : BackgroundService
{
    /// <summary>The time between passes.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<SearchBackfillService> _logger;

    public SearchBackfillService(IServiceScopeFactory scopes, ILogger<SearchBackfillService> logger)
    {
        _scopes = scopes;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                await using var scope = _scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<SearchBackfill>().RunAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogError(ex, "Search backfill failed; retrying in {Interval}", Interval);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
