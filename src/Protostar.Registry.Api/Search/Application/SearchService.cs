using Microsoft.EntityFrameworkCore;
using Protostar.Registry.Api.Infrastructure;

namespace Protostar.Registry.Api.Search;

/// <summary>Application service for searching an owner's sessions, memory and custom agents.</summary>
/// <remarks>
/// <para>
/// Postgres full-text search over <see cref="SearchEntry"/> rows: <c>websearch_to_tsquery</c> (so quotes make a phrase,
/// <c>or</c> an alternative and <c>-</c> an exclusion) against a stored <c>tsvector</c> with a GIN index, English
/// stemming. Each matching session, memory file or agent is one hit, its best-ranked entry (<c>ts_rank_cd</c>) standing for
/// it; hits are ordered by rank, then most recent first.
/// </para>
/// <para>
/// The snippet is <c>ts_headline</c> over the entry's text, which is redacted, so a snippet cannot show a secret the
/// redactor knows. Every query is filtered by owner, and the joins for names and dates are too.
/// </para>
/// <para>
/// Agents are searched only when asked for (<see cref="SearchSource.Agent"/>): a search with no kind covers sessions and
/// memory, as it did before agents were indexed, so a client that predates them never gets a hit it cannot show.
/// </para>
/// </remarks>
public sealed class SearchService
{
    /// <summary>The longest query accepted, in characters.</summary>
    public const int MaxQueryLength = 500;

    /// <summary>The most hits one search returns.</summary>
    public const int MaxLimit = 100;

    /// <summary>Marks the start of a match in a snippet.</summary>
    public const string MatchStart = "«";

    /// <summary>Marks the end of a match in a snippet.</summary>
    public const string MatchEnd = "»";

    private const string HeadlineOptions = "StartSel=" + MatchStart + ", StopSel=" + MatchEnd +
        ", MaxWords=30, MinWords=12, ShortWord=2, MaxFragments=2, FragmentDelimiter=\" ... \"";

    private readonly RegistryDbContext _db;

    public SearchService(RegistryDbContext db) => _db = db;

    /// <summary>The owner's sessions and memory files (or, when asked, agents) matching <paramref name="query"/>, best first.</summary>
    public async Task<SearchResult> SearchAsync(Guid ownerId, SearchQuery query, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query.Text))
            return SearchResult.Invalid("q is required: the words or phrase to search for.");
        if (query.Text.Length > MaxQueryLength)
            return SearchResult.Invalid($"q may be at most {MaxQueryLength} characters.");

        Guid[] projects = [];
        if (!string.IsNullOrWhiteSpace(query.Project))
        {
            projects = Guid.TryParse(query.Project, out var projectId)
                ? await _db.Projects.Where(p => p.OwnerId == ownerId && p.Id == projectId).Select(p => p.Id).ToArrayAsync(cancellationToken)
                : await _db.Projects.Where(p => p.OwnerId == ownerId && EF.Functions.ILike(p.Name, "%" + query.Project + "%")).Select(p => p.Id).ToArrayAsync(cancellationToken);
            if (projects.Length == 0)
                return SearchResult.Ok([]);
        }

        var source = query.Kind?.ToString() ?? string.Empty;
        var agentSource = nameof(SearchSource.Agent);
        var limit = Math.Clamp(query.Limit, 1, MaxLimit);
        var text = query.Text.Trim();

        // DISTINCT ON keeps each source's best entry; the outer query orders and limits before ts_headline runs, so
        // snippets are cut for the returned hits only.
        var rows = await _db.Database.SqlQuery<SearchRow>(
            $"""
            SELECT b."Source", b."TranscriptId", b."MemoryFileId", b."AgentId", b."MachineId", b."Rank",
                   ts_headline('english', b."Text", websearch_to_tsquery('english', {text}), {HeadlineOptions}) AS "Snippet"
            FROM (
                SELECT best.*, COALESCE(t."Summary_LastActivityAt", f."UpdatedAt", v."CreatedAt") AS "Date"
                FROM (
                    SELECT DISTINCT ON (COALESCE(e."TranscriptId", e."MemoryFileId", e."AgentId"))
                           e."Source", e."TranscriptId", e."MemoryFileId", e."AgentId", e."MachineId", e."Text",
                           ts_rank_cd(e."SearchVector", websearch_to_tsquery('english', {text}))::float8 AS "Rank"
                    FROM "SearchEntries" e
                    WHERE e."OwnerId" = {ownerId}
                      AND e."SearchVector" @@ websearch_to_tsquery('english', {text})
                      AND (({source} = '' AND e."Source" <> {agentSource}) OR e."Source" = {source})
                    ORDER BY COALESCE(e."TranscriptId", e."MemoryFileId", e."AgentId"), "Rank" DESC
                ) best
                LEFT JOIN "Transcripts" t ON t."Id" = best."TranscriptId" AND t."OwnerId" = {ownerId}
                LEFT JOIN "MemoryFiles" f ON f."Id" = best."MemoryFileId" AND f."OwnerId" = {ownerId}
                LEFT JOIN "Agents" a ON a."Id" = best."AgentId" AND a."CreatorId" = {ownerId}
                LEFT JOIN "AgentVersions" v ON v."Id" = a."CurrentVersionId"
                WHERE (t."Id" IS NOT NULL OR f."Id" IS NOT NULL OR a."Id" IS NOT NULL)
                  AND (cardinality({projects}) = 0 OR t."ProjectId" = ANY({projects}) OR f."ProjectId" = ANY({projects}) OR a."ProjectId" = ANY({projects}))
                ORDER BY "Rank" DESC, "Date" DESC NULLS LAST
                LIMIT {limit}
            ) b
            ORDER BY b."Rank" DESC, b."Date" DESC NULLS LAST
            """).ToListAsync(cancellationToken);

        return SearchResult.Ok(await ToHitsAsync(ownerId, rows, cancellationToken));
    }

    private async Task<IReadOnlyList<SearchHit>> ToHitsAsync(Guid ownerId, IReadOnlyList<SearchRow> rows, CancellationToken cancellationToken)
    {
        var transcriptIds = rows.Where(r => r.TranscriptId is not null).Select(r => r.TranscriptId!.Value).ToList();
        var transcripts = await _db.Transcripts.AsNoTracking()
            .Where(t => t.OwnerId == ownerId && transcriptIds.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, cancellationToken);

        var fileIds = rows.Where(r => r.MemoryFileId is not null).Select(r => r.MemoryFileId!.Value).ToList();
        var files = await _db.MemoryFiles.AsNoTracking()
            .Where(f => f.OwnerId == ownerId && fileIds.Contains(f.Id))
            .Select(f => new { f.Id, f.ProjectId, f.Path, f.UpdatedAt })
            .ToDictionaryAsync(f => f.Id, cancellationToken);

        var agentIds = rows.Where(r => r.AgentId is not null).Select(r => r.AgentId!.Value).ToList();
        var agents = await _db.Agents.AsNoTracking()
            .Where(a => a.CreatorId == ownerId && agentIds.Contains(a.Id) && a.CurrentVersion != null)
            .Select(a => new { a.Id, a.Name, a.ProjectId, a.CurrentVersion!.VersionNumber, a.CurrentVersion.Description, a.CurrentVersion.CreatedAt })
            .ToDictionaryAsync(a => a.Id, cancellationToken);

        var projectIds = transcripts.Values.Where(t => t.ProjectId is not null).Select(t => t.ProjectId!.Value)
            .Concat(files.Values.Select(f => f.ProjectId))
            .Concat(agents.Values.Where(a => a.ProjectId is not null).Select(a => a.ProjectId!.Value)).Distinct().ToList();
        var projectNames = await _db.Projects.AsNoTracking()
            .Where(p => p.OwnerId == ownerId && projectIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => p.Name, cancellationToken);

        var machineIds = rows.Where(r => r.MachineId is not null).Select(r => r.MachineId!.Value).Concat(transcripts.Values.Select(t => t.MachineId)).Distinct().ToList();
        var machineNames = await _db.Machines.AsNoTracking()
            .Where(m => m.OwnerId == ownerId && machineIds.Contains(m.Id))
            .ToDictionaryAsync(m => m.Id, m => m.Name, cancellationToken);

        var hits = new List<SearchHit>(rows.Count);
        foreach (var row in rows)
        {
            if (row.TranscriptId is { } transcriptId && transcripts.TryGetValue(transcriptId, out var t))
            {
                hits.Add(new SearchHit(
                    SearchSource.Session, t.Harness.Value, t.Key.Value, t.ParentKey?.Value,
                    SecretRedactor.RedactOrNull(t.Summary.Title ?? t.Summary.FirstPrompt), null,
                    t.ProjectId, t.ProjectId is { } p ? projectNames.GetValueOrDefault(p) : null,
                    t.MachineId, machineNames.GetValueOrDefault(t.MachineId), t.Summary.LastActivityAt, row.Snippet, row.Rank, null, null, null));
            }
            else if (row.MemoryFileId is { } fileId && files.TryGetValue(fileId, out var f))
            {
                hits.Add(new SearchHit(
                    SearchSource.Memory, null, null, null, null, f.Path.Value,
                    f.ProjectId, projectNames.GetValueOrDefault(f.ProjectId),
                    row.MachineId, row.MachineId is { } m ? machineNames.GetValueOrDefault(m) : null, f.UpdatedAt, row.Snippet, row.Rank, null, null, null));
            }
            else if (row.AgentId is { } agentId && agents.TryGetValue(agentId, out var a))
            {
                hits.Add(new SearchHit(
                    SearchSource.Agent, null, null, null, null, null,
                    a.ProjectId, a.ProjectId is { } p ? projectNames.GetValueOrDefault(p) : null,
                    null, null, a.CreatedAt, row.Snippet, row.Rank, a.Name, a.VersionNumber, SecretRedactor.Redact(a.Description)));
            }
        }

        return hits;
    }

    // One row of the search query. Property names match the SQL's column aliases.
    private sealed record SearchRow(string Source, Guid? TranscriptId, Guid? MemoryFileId, Guid? AgentId, Guid? MachineId, double Rank, string Snippet);
}

/// <summary>What to search for.</summary>
/// <param name="Text">The words or phrase, in web search syntax.</param>
/// <param name="Kind">Only sessions, only memory or only agents; <c>null</c> for sessions and memory.</param>
/// <param name="Project">Only this project: its id, or text its name contains.</param>
/// <param name="Limit">The most hits to return, 1 to <see cref="SearchService.MaxLimit"/>.</param>
public sealed record SearchQuery(string? Text, SearchSource? Kind, string? Project, int Limit);

/// <summary>One matching session, memory file or custom agent.</summary>
/// <param name="Kind">Session (a session or subagent transcript), memory or agent.</param>
/// <param name="Harness">For a session, its harness.</param>
/// <param name="Key">For a session, its key: open it with <c>GET /v1/transcripts/{harness}/{key}</c>.</param>
/// <param name="ParentKey">For a subagent transcript, the session it ran in.</param>
/// <param name="Title">For a session, its title or first prompt, redacted.</param>
/// <param name="Path">For memory, the file's path in the memory folder: read it with <c>GET /v1/memory/{projectId}/content?path=</c>.</param>
/// <param name="ProjectId">The project; <c>null</c> for a session not filed under one yet, or a personal agent.</param>
/// <param name="ProjectName">The project's display name.</param>
/// <param name="MachineId">For a session, the machine it came from; for memory, the machine that first sent the version matched; <c>null</c> for an agent.</param>
/// <param name="MachineName">That machine's name, or <c>null</c> once it is no longer registered.</param>
/// <param name="Date">For a session, its latest activity; for memory, when the file last changed; for an agent, when its current version was pushed.</param>
/// <param name="Snippet">Redacted text around the match, with each match between « and ».</param>
/// <param name="Rank">How well it matched; higher is better. Comparable only within one search.</param>
/// <param name="Name">For an agent, its name: read it with <c>GET /v1/agents/{name}/content</c> (with <c>projectId</c> for a project agent).</param>
/// <param name="Version">For an agent, its current version, the one searched.</param>
/// <param name="Description">For an agent, its current version's description, redacted.</param>
public sealed record SearchHit(
    SearchSource Kind, string? Harness, string? Key, string? ParentKey, string? Title, string? Path, Guid? ProjectId, string? ProjectName,
    Guid? MachineId, string? MachineName, DateTimeOffset? Date, string Snippet, double Rank, string? Name, int? Version, string? Description);

/// <summary>Hits, or why the search was refused.</summary>
public sealed record SearchResult(IReadOnlyList<SearchHit>? Hits, string? Problem)
{
    public static SearchResult Ok(IReadOnlyList<SearchHit> hits) => new(hits, null);

    public static SearchResult Invalid(string problem) => new(null, problem);
}
