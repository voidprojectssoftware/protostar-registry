using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Protostar.Registry.Api.Infrastructure;

namespace Protostar.Registry.Api.Sessions;

/// <summary>One pass of the sweep that deletes transcript chunk blobs no chunk row references.</summary>
/// <remarks>
/// <para>
/// Walks the store's listing under <see cref="TranscriptChunk.BlobKeyPrefix"/> a page at a time, asks the
/// database in one query which of that page's keys are referenced, and deletes what
/// <see cref="OrphanChunkBlobPolicy"/> calls orphans. Memory stays bounded by one page, whatever the number
/// of chunk rows or blobs.
/// </para>
/// <para>
/// Safe to run on several API instances at once without coordination. A key is generated fresh for each
/// append and never reused, and a referenced key only stops being referenced when its rows are deleted, so
/// every instance reaches the same verdict about a key, and deleting a key another instance already deleted
/// succeeds. What protects a blob whose row has not committed yet is the grace period, not locking.
/// </para>
/// </remarks>
public sealed class OrphanChunkBlobSweep
{
    private readonly RegistryDbContext _db;
    private readonly IBlobStore _blobs;
    private readonly ObjectStoreOptions _options;
    private readonly ILogger<OrphanChunkBlobSweep> _logger;

    public OrphanChunkBlobSweep(RegistryDbContext db, IBlobStore blobs, IOptions<ObjectStoreOptions> options, ILogger<OrphanChunkBlobSweep> logger)
    {
        _db = db;
        _blobs = blobs;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>Runs one pass over the whole store and returns what it saw and deleted.</summary>
    public async Task<OrphanChunkBlobSweepResult> RunAsync(CancellationToken cancellationToken)
    {
        var listed = 0;
        var deleted = 0;
        await foreach (var page in _blobs.ListAsync(TranscriptChunk.BlobKeyPrefix, cancellationToken))
        {
            listed += page.Count;
            var keys = page.Select(b => b.Key).ToList();
            var referenced = await _db.TranscriptChunks.AsNoTracking()
                .Where(c => keys.Contains(c.BlobKey))
                .Select(c => c.BlobKey)
                .ToListAsync(cancellationToken);

            var orphans = OrphanChunkBlobPolicy.SelectOrphans(
                page.Select(b => (b.Key, b.LastModified)), referenced.ToHashSet(StringComparer.Ordinal),
                DateTimeOffset.UtcNow, _options.OrphanSweepGrace);
            foreach (var key in orphans)
            {
                await _blobs.DeleteAsync(key, cancellationToken);
                _logger.LogInformation("Deleted orphaned chunk blob {BlobKey}", key);
                deleted++;
            }
        }

        _logger.LogInformation("Orphaned chunk blob sweep listed {Listed} blobs and deleted {Deleted}", listed, deleted);
        return new OrphanChunkBlobSweepResult(listed, deleted);
    }
}

/// <summary>What one sweep pass did.</summary>
/// <param name="Listed">Chunk blobs the store listed.</param>
/// <param name="Deleted">Orphans among them that the pass deleted; another instance sweeping at the same time may have deleted some of them first.</param>
public sealed record OrphanChunkBlobSweepResult(int Listed, int Deleted);

/// <summary>Runs <see cref="OrphanChunkBlobSweep"/> at startup and then every <see cref="ObjectStoreOptions.OrphanSweepInterval"/>.</summary>
/// <remarks>A failed pass is logged and the next one runs on schedule; one bad pass should not stop sweeping for the life of the process.</remarks>
public sealed class OrphanChunkBlobSweepService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly TimeSpan _interval;
    private readonly ILogger<OrphanChunkBlobSweepService> _logger;

    public OrphanChunkBlobSweepService(IServiceScopeFactory scopes, IOptions<ObjectStoreOptions> options, ILogger<OrphanChunkBlobSweepService> logger)
    {
        _scopes = scopes;
        _interval = options.Value.OrphanSweepInterval;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_interval);
        do
        {
            try
            {
                await using var scope = _scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<OrphanChunkBlobSweep>().RunAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogError(ex, "Orphaned chunk blob sweep failed; retrying in {Interval}", _interval);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
