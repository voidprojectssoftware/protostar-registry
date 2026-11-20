using System.ComponentModel;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using OpenIddict.Abstractions;
using Protostar.Registry.Api.Infrastructure;
using Protostar.Registry.Api.Sessions;
using Protostar.Registry.Api.Skills;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Protostar.Registry.Api.Memory;

/// <summary>The memory API: back up a project's agent memory files, and list what is kept.</summary>
/// <remarks>Every endpoint acts on the caller's own data only. The handlers stay thin; <see cref="MemoryService"/> does the work.</remarks>
public static class MemoryEndpoints
{
    private static readonly string PushDescription =
        $$"""
        Backs up the changed and deleted memory files of one project folder on one machine. `project` carries the
        folder's facts, resolved to a project exactly as a session's are, so memory and sessions share a project.

        Send only what changed. A file whose content the registry already holds as one of its versions stores
        nothing new (`unchanged`); new content becomes the next version, and earlier versions are kept. A path
        under `deleted` is marked deleted on this machine's copy; the file and its versions stay.

        At most {{MemoryLimits.MaxFilesPerPush}} files per push, sent and deleted together, each at most
        {{MemoryFileSizePolicy.MaxBytes / 1024}} KiB (problem `type` `{{MemoryProblemTypes.FileTooLarge}}`, status 413).
        A `400` with problem `type` `{{SessionProblemTypes.UnknownMachine}}` means `machineId` is not registered:
        register it with `PUT /v1/machines/{machineId}` and retry.
        """;

    public static void MapMemoryEndpoints(this WebApplication app)
    {
        var memory = app.MapGroup("/v1/memory").RequireAuthorization(SkillEndpoints.Policy).DisableAntiforgery().WithTags(ApiTags.Memory);

        memory.MapPost("", PushAsync)
            .WithName("PushMemory")
            .WithSummary("Back up a project's memory files")
            .WithDescription(PushDescription)
            .WithMetadata(new RequestSizeLimitAttribute(MemoryLimits.MaxRequestBytes))
            .Produces<MemoryPushResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge);
        memory.MapGet("", ListProjectsAsync)
            .WithName("ListMemoryProjects")
            .WithSummary("List my projects that have memory")
            .WithDescription("One row per project with backed-up memory, most recently changed first.")
            .Produces<MemoryProjectResponse[]>();
        memory.MapGet("/{projectId:guid}", ListFilesAsync)
            .WithName("ListProjectMemory")
            .WithSummary("List a project's memory files")
            .WithDescription($"Every memory file of the project, with its versions and each machine's copy (the version it last sent, and when it was deleted there). No content. A `404` with problem `type` `{MemoryProblemTypes.UnknownProject}` means the project is not yours or does not exist.")
            .Produces<MemoryProjectFilesResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    private static async Task<IResult> PushAsync(MemoryPushRequest request, ClaimsPrincipal user, MemoryService service, CancellationToken cancellationToken)
    {
        if (!TryGetOwner(user, out var ownerId))
            return Results.Unauthorized();

        var result = await service.PushAsync(ownerId, request, cancellationToken);
        return result.Failure switch
        {
            MemoryFailure.None => Results.Ok(MemoryPushResponse.From(result.Value!)),
            MemoryFailure.UnknownMachine => Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: result.Message, type: SessionProblemTypes.UnknownMachine),
            MemoryFailure.TooLarge => Results.Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: result.Message, type: MemoryProblemTypes.FileTooLarge),
            _ => Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: result.Message),
        };
    }

    private static async Task<IResult> ListProjectsAsync(ClaimsPrincipal user, MemoryService service, CancellationToken cancellationToken)
    {
        if (!TryGetOwner(user, out var ownerId))
            return Results.Unauthorized();

        var projects = await service.ListProjectsAsync(ownerId, cancellationToken);
        return Results.Ok(projects.Select(MemoryProjectResponse.From).ToArray());
    }

    private static async Task<IResult> ListFilesAsync(
        [Description("The project, as `GET /v1/memory` or `protostar project` shows it.")] Guid projectId,
        ClaimsPrincipal user, MemoryService service, CancellationToken cancellationToken)
    {
        if (!TryGetOwner(user, out var ownerId))
            return Results.Unauthorized();

        var files = await service.ListFilesAsync(ownerId, projectId, cancellationToken);
        return files is null
            ? Results.Problem(statusCode: StatusCodes.Status404NotFound, title: $"No project {projectId}.", type: MemoryProblemTypes.UnknownProject)
            : Results.Ok(MemoryProjectFilesResponse.From(files));
    }

    private static bool TryGetOwner(ClaimsPrincipal user, out Guid ownerId) =>
        Guid.TryParse(user.GetClaim(Claims.Subject), out ownerId);
}

/// <summary>Problem <c>type</c> URIs of the memory API (the registry#41 convention: clients branch on these, not on titles).</summary>
public static class MemoryProblemTypes
{
    /// <summary>A pushed file is over the size limit (status <c>413</c>).</summary>
    public const string FileTooLarge = "https://protostar.dev/problems/memory-file-too-large";

    /// <summary>No project with that id among the caller's (status <c>404</c>).</summary>
    public const string UnknownProject = "https://protostar.dev/problems/unknown-project";
}

/// <summary>What a memory push did.</summary>
/// <param name="ProjectId">The project the memory was filed under.</param>
/// <param name="ProjectName">Its display name.</param>
/// <param name="Folder">The folder this machine's copy is filed by: the main worktree for git.</param>
/// <param name="Files">Each file's outcome, sent files first, then deletions, in request order.</param>
public sealed record MemoryPushResponse(Guid ProjectId, string ProjectName, string Folder, IReadOnlyList<MemoryFileOutcomeResponse> Files)
{
    internal static MemoryPushResponse From(MemoryPushResult r) =>
        new(r.ProjectId, r.ProjectName, r.Folder, r.Files.Select(f => new MemoryFileOutcomeResponse(f.Path, f.Change, f.Version)).ToList());
}

/// <summary>One file's outcome.</summary>
/// <param name="Path">The file's path.</param>
/// <param name="Change">`created`, `versioned`, `unchanged`, `markedDeleted`, or `unknown` (a deletion of a file this copy never backed up).</param>
/// <param name="Version">The version this machine's copy now points at; absent for `unknown`.</param>
public sealed record MemoryFileOutcomeResponse(string Path, MemoryFileChange Change, int? Version);

/// <summary>A project that has memory.</summary>
/// <param name="ProjectId">The project.</param>
/// <param name="ProjectName">Its display name.</param>
/// <param name="Files">How many memory files it has, deleted ones included.</param>
/// <param name="Versions">How many versions across them.</param>
/// <param name="Machines">How many machines have backed up memory for it.</param>
/// <param name="UpdatedAt">When any of its files last changed.</param>
public sealed record MemoryProjectResponse(Guid ProjectId, string ProjectName, int Files, int Versions, int Machines, DateTimeOffset UpdatedAt)
{
    internal static MemoryProjectResponse From(MemoryProjectSummary s) => new(s.ProjectId, s.ProjectName, s.Files, s.Versions, s.Machines, s.UpdatedAt);
}

/// <summary>A project's memory files, by path.</summary>
public sealed record MemoryProjectFilesResponse(Guid ProjectId, string ProjectName, IReadOnlyList<MemoryFileView> Files)
{
    internal static MemoryProjectFilesResponse From(MemoryProjectFiles f) => new(f.ProjectId, f.ProjectName, f.Files);
}
