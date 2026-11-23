using System.Text;
using Microsoft.EntityFrameworkCore;
using Protostar.Registry.Api.Agents;
using Protostar.Registry.Api.Common;
using Protostar.Registry.Api.Infrastructure;
using Protostar.Registry.Api.Memory;
using Protostar.Registry.Api.Sessions;

namespace Protostar.Registry.Api.Search;

/// <summary>Writes the search index entries of one transcript chunk, memory file or custom agent, replacing any it had.</summary>
/// <remarks>
/// Used by the event handlers as content arrives, and by the reprocessing pass (<see cref="TranscriptReprocessor"/> and
/// <see cref="SearchBackfill"/>) for content that arrived before the index existed or was indexed by an older
/// <see cref="SearchEntry.CurrentIndexVersion"/>. Nothing here logs
/// content: only ids and counts.
/// </remarks>
public sealed class SearchIndexer
{
    private readonly RegistryDbContext _db;

    public SearchIndexer(RegistryDbContext db) => _db = db;

    /// <summary>Indexes a chunk's readable text, replacing the chunk's earlier entries.</summary>
    /// <remarks>
    /// When <c>startsRevision</c> is set the chunk begins a new revision, and every entry of the transcript's earlier
    /// chunks is removed too, so the index covers only the current revision, as listings do.
    /// </remarks>
    public async Task IndexChunkAsync(
        Guid ownerId, Guid transcriptId, Guid chunkId, Guid machineId, bool startsRevision, string? text, CancellationToken cancellationToken)
    {
        var entries = SearchEntry.ForChunk(ownerId, transcriptId, chunkId, machineId, text, DateTimeOffset.UtcNow);
        if (startsRevision)
            await _db.SearchEntries.Where(e => e.TranscriptId == transcriptId).ExecuteDeleteAsync(cancellationToken);
        else
            await _db.SearchEntries.Where(e => e.ChunkId == chunkId).ExecuteDeleteAsync(cancellationToken);
        await SaveAsync(entries, cancellationToken);
    }

    /// <summary>Indexes a memory file's current version, replacing its earlier entries.</summary>
    /// <remarks>
    /// A file deleted on every machine that had it gets one empty entry: it stays backed up but no longer turns up in
    /// search. Content that is not text (it holds a NUL byte) is not searchable either. Does nothing for a file that
    /// no longer exists.
    /// </remarks>
    public async Task IndexMemoryFileAsync(Guid memoryFileId, CancellationToken cancellationToken)
    {
        var file = await _db.MemoryFiles.AsNoTracking().Include(f => f.Copies)
            .FirstOrDefaultAsync(f => f.Id == memoryFileId, cancellationToken);
        if (file is null)
            return;

        var current = file.Current();
        var version = await _db.MemoryFileVersions.AsNoTracking()
            .Where(v => v.MemoryFileId == file.Id && v.Number == current.Version)
            .Select(v => new { v.Content, v.MachineId })
            .SingleAsync(cancellationToken);

        var text = current.DeletedEverywhere || Array.IndexOf(version.Content, (byte)0) >= 0
            ? null
            : Encoding.UTF8.GetString(version.Content);
        var entries = SearchEntry.ForMemoryFile(file.OwnerId, file.Id, file.UpdatedAt, version.MachineId, text, DateTimeOffset.UtcNow);

        await _db.SearchEntries.Where(e => e.MemoryFileId == file.Id).ExecuteDeleteAsync(cancellationToken);
        await SaveAsync(entries, cancellationToken);
    }

    /// <summary>Indexes an agent's current version, its whole file (front matter included), replacing its earlier entries.</summary>
    /// <remarks>Does nothing for an agent that no longer exists. A deleted agent's entries go with it (the foreign key cascades).</remarks>
    public async Task IndexAgentAsync(Guid agentId, CancellationToken cancellationToken)
    {
        var current = await _db.Agents.AsNoTracking()
            .Where(a => a.Id == agentId && a.CurrentVersion != null)
            .Select(a => new { a.CreatorId, VersionId = a.CurrentVersion!.Id, a.CurrentVersion.Content })
            .FirstOrDefaultAsync(cancellationToken);
        if (current is null)
            return;

        var entries = SearchEntry.ForAgent(current.CreatorId, agentId, current.VersionId, Encoding.UTF8.GetString(current.Content), DateTimeOffset.UtcNow);
        await _db.SearchEntries.Where(e => e.AgentId == agentId).ExecuteDeleteAsync(cancellationToken);
        await SaveAsync(entries, cancellationToken);
    }

    // Saves only the new entries. On failure they are detached, so the caller's unit of work is left as it was.
    private async Task SaveAsync(IReadOnlyList<SearchEntry> entries, CancellationToken cancellationToken)
    {
        _db.SearchEntries.AddRange(entries);
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        finally
        {
            foreach (var entry in _db.ChangeTracker.Entries<SearchEntry>().ToList())
                entry.State = EntityState.Detached;
        }
    }
}

/// <summary>Indexes each appended transcript chunk from the text its event carries.</summary>
/// <remarks>
/// Runs after the append has committed. A failure here must not fail the append, which already stored the chunk, so
/// it is logged (ids only) and the chunk is left for <see cref="TranscriptReprocessor"/>, which finds chunks with no entry.
/// </remarks>
public sealed class IndexAppendedChunk : IDomainEventHandler<TranscriptChunkAppended>
{
    private readonly SearchIndexer _indexer;
    private readonly ILogger<IndexAppendedChunk> _logger;

    public IndexAppendedChunk(SearchIndexer indexer, ILogger<IndexAppendedChunk> logger)
    {
        _indexer = indexer;
        _logger = logger;
    }

    public async Task HandleAsync(TranscriptChunkAppended domainEvent, CancellationToken cancellationToken)
    {
        try
        {
            await _indexer.IndexChunkAsync(
                domainEvent.OwnerId, domainEvent.TranscriptId, domainEvent.ChunkId, domainEvent.MachineId,
                domainEvent.StartsRevision, domainEvent.Text, cancellationToken);
        }
        catch (Exception ex) when (ex is DbUpdateException or Npgsql.NpgsqlException)
        {
            _logger.LogWarning(ex, "Could not index chunk {ChunkId} of transcript {TranscriptId}; reprocessing will retry it",
                domainEvent.ChunkId, domainEvent.TranscriptId);
        }
    }
}

/// <summary>Re-indexes a memory file whenever it changes.</summary>
/// <remarks>As <see cref="IndexAppendedChunk"/>: a failure is logged and left for <see cref="SearchBackfill"/>, which finds stale files.</remarks>
public sealed class IndexChangedMemoryFile : IDomainEventHandler<MemoryFileChanged>
{
    private readonly SearchIndexer _indexer;
    private readonly ILogger<IndexChangedMemoryFile> _logger;

    public IndexChangedMemoryFile(SearchIndexer indexer, ILogger<IndexChangedMemoryFile> logger)
    {
        _indexer = indexer;
        _logger = logger;
    }

    public async Task HandleAsync(MemoryFileChanged domainEvent, CancellationToken cancellationToken)
    {
        try
        {
            await _indexer.IndexMemoryFileAsync(domainEvent.MemoryFileId, cancellationToken);
        }
        catch (Exception ex) when (ex is DbUpdateException or Npgsql.NpgsqlException)
        {
            _logger.LogWarning(ex, "Could not index memory file {MemoryFileId}; the search backfill will retry it", domainEvent.MemoryFileId);
        }
    }
}

/// <summary>Re-indexes a custom agent whenever a new version of it is pushed.</summary>
/// <remarks>As <see cref="IndexAppendedChunk"/>: a failure is logged and left for <see cref="SearchBackfill"/>, which finds stale agents.</remarks>
public sealed class IndexPushedAgent : IDomainEventHandler<AgentVersionPushed>
{
    private readonly SearchIndexer _indexer;
    private readonly ILogger<IndexPushedAgent> _logger;

    public IndexPushedAgent(SearchIndexer indexer, ILogger<IndexPushedAgent> logger)
    {
        _indexer = indexer;
        _logger = logger;
    }

    public async Task HandleAsync(AgentVersionPushed domainEvent, CancellationToken cancellationToken)
    {
        try
        {
            await _indexer.IndexAgentAsync(domainEvent.AgentId, cancellationToken);
        }
        catch (Exception ex) when (ex is DbUpdateException or Npgsql.NpgsqlException)
        {
            _logger.LogWarning(ex, "Could not index agent {AgentId}; the search backfill will retry it", domainEvent.AgentId);
        }
    }
}
