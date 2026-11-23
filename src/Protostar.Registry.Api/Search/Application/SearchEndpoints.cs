using System.ComponentModel;
using System.Security.Claims;
using OpenIddict.Abstractions;
using Protostar.Registry.Api.Infrastructure;
using Protostar.Registry.Api.Skills;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Protostar.Registry.Api.Search;

/// <summary>The search API: find my sessions, memory and custom agents by what they say.</summary>
/// <remarks>Acts on the caller's own data only. The handler stays thin; <see cref="SearchService"/> does the work.</remarks>
public static class SearchEndpoints
{
    private static readonly string SearchDescription =
        $"""
        Searches the text of every session (and subagent) transcript and every memory file backed up from any of your
        machines. One hit per session or memory file, best match first, then most recent.

        `q` uses web search syntax: words are all required, `"quoted words"` must appear as a phrase, `or` gives an
        alternative and `-word` excludes. Matching is by English word stems and ignores case.

        Sessions are searched by what the user typed, the assistant's text replies and the names of the tools it called
        (not tool inputs or results); memory by each file's current version. Known secret patterns (keys, tokens,
        passwords, private keys) are redacted before anything is indexed, so searching for a secret's value finds
        nothing, and a snippet shows a marker such as `[redacted:github-token]` instead. Memory deleted on every machine
        that had it is not searched.

        Each snippet marks matches between `{SearchService.MatchStart}` and `{SearchService.MatchEnd}`. No match is an
        empty array, not an error. Content backed up before search existed is indexed in the background after an
        upgrade, so it may take a moment to appear.

        `kind=agent` searches your custom agents instead: each agent's current version, its whole file (front matter
        included), redacted like everything else. An agent hit has `name`, `version` and `description` in place of a
        session's key or a memory file's path, no machine, and `date` is when that version was pushed. Agents are only
        searched with `kind=agent`; without `kind`, a search covers sessions and memory.
        """;

    public static void MapSearchEndpoints(this WebApplication app)
    {
        app.MapGet("/v1/search", SearchAsync)
            .RequireAuthorization(SkillEndpoints.Policy)
            .WithTags(ApiTags.Search)
            .WithName("Search")
            .WithSummary("Search my sessions, memory and agents")
            .WithDescription(SearchDescription)
            .Produces<SearchHit[]>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .WithResponseDescription(StatusCodes.Status400BadRequest, $"`q` is missing, empty or over {SearchService.MaxQueryLength} characters, or `kind` is not `session`, `memory` or `agent`.");
    }

    private static async Task<IResult> SearchAsync(
        ClaimsPrincipal user, SearchService service, CancellationToken cancellationToken,
        [Description("The words or phrase to search for, in web search syntax.")] string? q = null,
        [Description("`session`, `memory` or `agent`; sessions and memory when absent.")] string? kind = null,
        [Description("Only this project: its id, or text its name contains, for example `protostar`.")] string? project = null,
        [Description("The most hits to return, 1 to 100.")] int limit = 20)
    {
        if (!Guid.TryParse(user.GetClaim(Claims.Subject), out var ownerId))
            return Results.Unauthorized();

        // Parsed here rather than bound as the enum, which binds case-sensitively and so would refuse `session`.
        SearchSource? source = null;
        if (kind is not null)
        {
            if (!Enum.TryParse<SearchSource>(kind, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed))
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "kind must be session, memory or agent.");
            source = parsed;
        }

        var result = await service.SearchAsync(ownerId, new SearchQuery(q, source, project, limit), cancellationToken);
        return result.Hits is { } hits
            ? Results.Ok(hits)
            : Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: result.Problem);
    }
}
