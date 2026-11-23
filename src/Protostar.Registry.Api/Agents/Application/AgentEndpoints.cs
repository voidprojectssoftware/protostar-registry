using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using OpenIddict.Abstractions;
using Protostar.Registry.Api.Infrastructure;
using Protostar.Registry.Api.Projects;
using Protostar.Registry.Api.Skills;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Protostar.Registry.Api.Agents;

/// <summary>The custom agents API: push one agent, list my agents, and rename or delete one.</summary>
/// <remarks>
/// Shaped like the skills API so a client reuses its code (ADR 0003): the push is <c>multipart/form-data</c> with the
/// same <c>project</c> and <c>baseVersion</c> fields, but exactly one file part, whose filename is the file's path
/// relative to the agents folder. The 409 problems carry the same field names as the skill ones under their own types.
/// There is no bulk push: that exists for skills only because a skill is a folder of parts. An agent is personal unless
/// it belongs to a project (ADR 0002).
/// </remarks>
public static class AgentEndpoints
{
    // The one file plus multipart framing.
    private const long UploadMaxBytes = AgentSizePolicy.MaxFileBytes + (64L * 1024);

    private static readonly JsonSerializerOptions ProjectJson = new(JsonSerializerDefaults.Web);

    public static void MapAgentEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/v1/agents")
            .RequireAuthorization(SkillEndpoints.Policy)
            .DisableAntiforgery()
            .WithTags(ApiTags.Agents);

        group.MapPost("", PushAsync)
            .WithName("PushAgent")
            .WithSummary("Push one agent")
            .WithDescription(
                "Uploads one custom agent as multipart/form-data with exactly one file part, whose filename is the file's path " +
                "relative to the agents folder (`reviewer.md`, or `review/reviewer.md` in a subfolder). The agent's name is the " +
                "`name` field of its YAML front matter, not its file name; a file without front matter, `name` or `description` " +
                "is refused with 400. An optional `project` field makes it a project agent, exactly as for skills; without it the " +
                "agent is personal. Returns 201 when a new version is stored, or 200 when the file matches the current version " +
                "byte-for-byte. An optional `baseVersion` names the version the copy was last synced from (0 when never): when it " +
                $"is not current and the content differs, the answer is 409 with problem `type` `{AgentProblemTypes.StaleVersion}` " +
                "and `currentVersion`. When the name was deleted or renamed away and no agent has taken it, a `baseVersion` from 1 " +
                $"up to its last version is refused with 409 `{AgentProblemTypes.Deleted}` or `{AgentProblemTypes.Renamed}`, " +
                "carrying `lastVersion` and, for a rename, `renamedTo`.")
            .WithMetadata(new RequestSizeLimitAttribute(UploadMaxBytes))
            .Produces<AgentPushResponse>(StatusCodes.Status201Created)
            .Produces<AgentPushResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge);

        group.MapGet("", ListAsync)
            .WithName("ListAgents")
            .WithSummary("List my agents")
            .WithDescription(
                "Lists the caller's personal agents, each described by its current version: name, description, path, project, " +
                "version, content hash, push time, size and first version. `includeProjects=true` adds every project's agents; " +
                "`projectId` lists only that project's. `removed` lists, under the same filter, the names deleted or renamed away " +
                "that no agent has taken since. The content hash is the uppercase hex SHA-256 of the file's bytes alone.")
            .Produces<AgentListResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        group.MapPost("/{name}/rename", RenameAsync)
            .WithName("RenameAgent")
            .WithSummary("Rename an agent")
            .WithDescription(
                "Renames the caller's agent, keeping its id and every version. Omit `projectId` for the personal agent of that " +
                "name. The body is `{\"newName\": \"...\"}`. 409 when the caller already has an agent of that name in the same " +
                "scope. The stored files keep the name they were pushed with. The old name is remembered (see `removed`).")
            .Produces<AgentRenameResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapDelete("/{name}", DeleteAsync)
            .WithName("DeleteAgent")
            .WithSummary("Delete an agent")
            .WithDescription(
                "Deletes the caller's agent with every version. Unrecoverable. Omit `projectId` for the personal agent of that " +
                "name. Returns 204. The name is remembered (see `removed`), so a push from a copy of it is refused.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    private static async Task<IResult> PushAsync(
        IFormFileCollection files,
        [FromForm] string? project,
        [FromForm] string? baseVersion,
        ClaimsPrincipal user,
        AgentPushService pushService,
        CancellationToken cancellationToken)
    {
        if (!TryGetCreator(user, out var creatorId))
            return Results.Unauthorized();

        // Bound as text and parsed here, as the skill push does, so a malformed number is a 400 rather than a framework error.
        int? based = null;
        if (Blank(baseVersion) is { } text)
        {
            if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "baseVersion must be 0 or a version number.");
            based = number;
        }

        ProjectFolderRequest? folder = null;
        if (Blank(project) is { } json && !TryReadProject(json, out folder))
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "The project field must be JSON: {\"machineId\": \"<guid>\", \"facts\": {...}}.");

        if (files.Count != 1)
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: $"An agent push carries exactly one file part; this one had {files.Count}.");

        var file = files[0];
        using var buffer = new MemoryStream();
        await using (var stream = file.OpenReadStream())
            await stream.CopyToAsync(buffer, cancellationToken);
        var path = !string.IsNullOrWhiteSpace(file.FileName) ? file.FileName : file.Name;

        var result = await pushService.PushAsync(creatorId, folder, path, buffer.ToArray(), based, cancellationToken);
        return result.Status switch
        {
            AgentPushStatus.Created => Results.Created($"/v1/agents/{Uri.EscapeDataString(result.Name!)}", ToResponse(result)),
            AgentPushStatus.Unchanged => Results.Ok(ToResponse(result)),
            _ => Failure(result),
        };
    }

    private static async Task<IResult> ListAsync(
        [FromQuery] bool? includeProjects,
        [FromQuery] Guid? projectId,
        ClaimsPrincipal user,
        AgentQueryService queries,
        CancellationToken cancellationToken)
    {
        if (!TryGetCreator(user, out var creatorId))
            return Results.Unauthorized();

        var filter = new AgentListFilter(includeProjects ?? false, projectId);
        var agents = await queries.ListAsync(creatorId, filter, cancellationToken);
        var removed = await queries.ListRemovedAsync(creatorId, filter, cancellationToken);
        return Results.Ok(new AgentListResponse(
            agents.Select(a => new AgentListItem(
                a.Name, a.Description, a.Path, a.Version, a.ContentHash, a.UpdatedAt, a.Size, a.ProjectId, a.ProjectName, a.FirstVersion)).ToArray(),
            removed.Select(r => new AgentRemovedItem(r.Name, r.ProjectId, r.LastVersion, r.RenamedTo, r.RemovedAt)).ToArray()));
    }

    private static async Task<IResult> RenameAsync(
        string name, [FromQuery] Guid? projectId, AgentRenameRequest request, ClaimsPrincipal user, AgentChangeService changes, CancellationToken cancellationToken)
    {
        if (!TryGetCreator(user, out var creatorId))
            return Results.Unauthorized();

        var result = await changes.RenameAsync(creatorId, projectId, name, request.NewName ?? "", cancellationToken);
        return result.Error is null ? Results.Ok(new AgentRenameResponse(name, result.Name!, projectId)) : ChangeFailure(result);
    }

    private static async Task<IResult> DeleteAsync(
        string name, [FromQuery] Guid? projectId, ClaimsPrincipal user, AgentChangeService changes, CancellationToken cancellationToken)
    {
        if (!TryGetCreator(user, out var creatorId))
            return Results.Unauthorized();

        var result = await changes.DeleteAsync(creatorId, projectId, name, cancellationToken);
        return result.Error is null ? Results.NoContent() : ChangeFailure(result);
    }

    private static IResult ChangeFailure(AgentChangeResult result) => Results.Problem(
        statusCode: result.Error switch
        {
            AgentChangeError.NotFound => StatusCodes.Status404NotFound,
            AgentChangeError.NameTaken => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status400BadRequest,
        },
        title: result.Message);

    private static IResult Failure(AgentPushResult result)
    {
        if (result.Error == AgentPushError.StaleVersion)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: result.Message,
                type: AgentProblemTypes.StaleVersion,
                extensions: new Dictionary<string, object?> { ["currentVersion"] = result.CurrentVersion, ["projectId"] = result.Project?.ProjectId });
        }

        if (result.Error == AgentPushError.Removed)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: result.Message,
                type: result.RenamedTo is null ? AgentProblemTypes.Deleted : AgentProblemTypes.Renamed,
                extensions: new Dictionary<string, object?>
                {
                    ["lastVersion"] = result.LastVersion,
                    ["renamedTo"] = result.RenamedTo,
                    ["projectId"] = result.Project?.ProjectId,
                });
        }

        var status = result.Error switch
        {
            AgentPushError.UnknownCreator => StatusCodes.Status401Unauthorized,
            AgentPushError.NotCreator => StatusCodes.Status403Forbidden,
            AgentPushError.TooLarge => StatusCodes.Status413PayloadTooLarge,
            _ => StatusCodes.Status400BadRequest,
        };
        return Results.Problem(statusCode: status, title: result.Message);
    }

    private static AgentPushResponse ToResponse(AgentPushResult result) => new(
        result.Status.ToString().ToLowerInvariant(), result.Name, result.Version, result.AgentId, result.VersionId, result.Path,
        result.Project?.ProjectId, result.Project?.Name);

    private static bool TryReadProject(string json, out ProjectFolderRequest? project)
    {
        try
        {
            project = JsonSerializer.Deserialize<ProjectFolderRequest>(json, ProjectJson);
            return project is { Facts: not null } && project.MachineId != Guid.Empty;
        }
        catch (JsonException)
        {
            project = null;
            return false;
        }
    }

    private static bool TryGetCreator(ClaimsPrincipal user, out Guid creatorId) =>
        Guid.TryParse(user.GetClaim(Claims.Subject), out creatorId);

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}

/// <summary>Problem <c>type</c> URIs that identify an agent push refusal; clients branch on these, never on the title.</summary>
public static class AgentProblemTypes
{
    /// <summary>A push named a base version that is not the agent's current version (409); carries <c>currentVersion</c> and <c>projectId</c>.</summary>
    public const string StaleVersion = "https://protostar.dev/problems/stale-agent-version";

    /// <summary>A push came from a copy of an agent deleted since it synced (409); carries <c>lastVersion</c> and <c>projectId</c>.</summary>
    public const string Deleted = "https://protostar.dev/problems/agent-deleted";

    /// <summary>A push came from a copy of an agent renamed since it synced (409); carries <c>lastVersion</c>, <c>renamedTo</c> and <c>projectId</c>.</summary>
    public const string Renamed = "https://protostar.dev/problems/agent-renamed";
}

/// <summary>The JSON shape returned for a pushed agent.</summary>
/// <remarks><c>ProjectId</c> and <c>ProjectName</c> are null for a personal agent. <c>Path</c> is the current version's.</remarks>
public sealed record AgentPushResponse(
    string Status, string? Name, int? Version, Guid? AgentId, Guid? VersionId, string? Path, Guid? ProjectId, string? ProjectName);

/// <summary>The JSON shape returned by the list endpoint.</summary>
public sealed record AgentListResponse(IReadOnlyList<AgentListItem> Agents, IReadOnlyList<AgentRemovedItem> Removed);

/// <summary>One agent in <see cref="AgentListResponse"/>, described by its current version.</summary>
public sealed record AgentListItem(
    string Name, string Description, string Path, int Version, string ContentHash, DateTimeOffset UpdatedAt, long Size,
    Guid? ProjectId, string? ProjectName, int FirstVersion);

/// <summary>One name in <see cref="AgentListResponse.Removed"/>: deleted, or renamed to <c>RenamedTo</c>.</summary>
public sealed record AgentRemovedItem(string Name, Guid? ProjectId, int LastVersion, string? RenamedTo, DateTimeOffset RemovedAt);

/// <summary>The JSON body of a rename.</summary>
public sealed record AgentRenameRequest(string? NewName);

/// <summary>The JSON shape returned by a rename: the name before and after, and the project.</summary>
public sealed record AgentRenameResponse(string OldName, string Name, Guid? ProjectId);
