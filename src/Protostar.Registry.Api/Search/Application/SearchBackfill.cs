using Microsoft.EntityFrameworkCore;
using Protostar.Registry.Api.Infrastructure;
using Protostar.Registry.Api.Sessions;

namespace Protostar.Registry.Api.Search;

/// <summary>The part of the reprocessing pass that keeps the search index's memory and agent entries current and drops entries of superseded revisions.</summary>
/// <remarks>
/// <para>
/// Missing means a memory file with no entry of <see cref="SearchEntry.CurrentIndexVersion"/> made from its latest
/// change, and an agent with no entry of that version made from its current version. Entries left from a transcript's earlier revisions are removed. Transcript chunks are indexed by
/// <see cref="TranscriptReprocessor"/>, which reads each transcript's chunks once for both its summary and its entries.
/// </para>
/// <para>
/// A file that fails is logged by id, skipped for the rest of the pass, and tried again on the next one. Safe to run
/// on several instances at once: entries are unique per source and segment, so a duplicate write fails and is skipped.
/// </para>
/// </remarks>
public sealed class SearchBackfill
{
    private const int BatchSize = 20;

    private readonly RegistryDbContext _db;
    private readonly SearchIndexer _indexer;
    private readonly ILogger<SearchBackfill> _logger;

    public SearchBackfill(RegistryDbContext db, SearchIndexer indexer, ILogger<SearchBackfill> logger)
    {
        _db = db;
        _indexer = indexer;
        _logger = logger;
    }

    /// <summary>Runs one pass and returns what it did.</summary>
    public async Task<SearchBackfillResult> RunAsync(CancellationToken cancellationToken)
    {
        var superseded = await _db.SearchEntries
            .Where(e => e.ChunkId != null && _db.TranscriptChunks.Any(c => c.Id == e.ChunkId &&
                _db.Transcripts.Any(t => t.Id == c.TranscriptId && t.Revision != c.Revision)))
            .ExecuteDeleteAsync(cancellationToken);

        var files = await IndexMemoryFilesAsync(cancellationToken);
        var agents = await IndexAgentsAsync(cancellationToken);
        _logger.LogInformation(
            "Search backfill indexed {Files} memory files and {Agents} agents and removed {Superseded} entries of earlier revisions",
            files, agents, superseded);
        return new SearchBackfillResult(files, superseded, agents);
    }

    private async Task<int> IndexAgentsAsync(CancellationToken cancellationToken)
    {
        var failed = new HashSet<Guid>();
        var indexed = 0;
        while (true)
        {
            var batch = await _db.Agents.AsNoTracking()
                .Where(a => a.CurrentVersionId != null && !failed.Contains(a.Id) &&
                    !_db.SearchEntries.Any(e => e.AgentId == a.Id && e.IndexVersion == SearchEntry.CurrentIndexVersion && e.AgentVersionId == a.CurrentVersionId))
                .OrderBy(a => a.CreatedAt)
                .Select(a => a.Id)
                .Take(BatchSize)
                .ToListAsync(cancellationToken);
            if (batch.Count == 0)
                return indexed;

            foreach (var id in batch)
            {
                try
                {
                    await _indexer.IndexAgentAsync(id, cancellationToken);
                    indexed++;
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                {
                    failed.Add(id);
                    _logger.LogWarning(ex, "Search backfill could not index agent {AgentId}; it will be retried on the next pass", id);
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
}

/// <summary>What one backfill pass did.</summary>
/// <param name="MemoryFiles">Memory files indexed.</param>
/// <param name="Superseded">Entries removed because their chunk belongs to an earlier revision.</param>
/// <param name="Agents">Agents indexed.</param>
public sealed record SearchBackfillResult(int MemoryFiles, int Superseded, int Agents);
