using System.Security.Claims;
using OpenIddict.Abstractions;
using Protostar.Registry.Api.Infrastructure;
using Protostar.Registry.Api.Skills;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Protostar.Registry.Api.Projects;

/// <summary>The project API: which project a folder belongs to, and linking or unlinking a folder.</summary>
/// <remarks>
/// Each call carries the client's <see cref="ProjectFacts"/> for the folder and is answered by the same
/// matching that files sessions (<see cref="ProjectService"/>), so the answer is where the folder's sessions go.
/// </remarks>
public static class ProjectEndpoints
{
    public static void MapProjectEndpoints(this WebApplication app)
    {
        var projects = app.MapGroup("/v1/projects").RequireAuthorization(SkillEndpoints.Policy).DisableAntiforgery().WithTags(ApiTags.Projects);

        projects.MapPost("/resolve", ResolveAsync)
            .WithName("ResolveProject")
            .WithSummary("Which project is this folder in")
            .WithDescription("Resolves a folder's facts to a project without changing anything. A folder no backup has created a project for yet has no `projectId`; folders outside git also get suggested projects to link, never applied.")
            .Produces<ProjectViewResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest);
        projects.MapPost("/link", LinkAsync)
            .WithName("LinkFolder")
            .WithSummary("Link a folder to a project")
            .WithDescription("Links the folder on this machine to a project, by id or exact name. Its sessions move with it, and it resolves to that project until unlinked. Its project skills move too: all of them when the folder had a project of its own, otherwise only those an earlier link or unlink of this folder carried in. 409 when the target already has a skill of the same name; then nothing changes.")
            .Produces<ProjectLinkResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
        projects.MapPost("/unlink", UnlinkAsync)
            .WithName("UnlinkFolder")
            .WithSummary("Unlink a folder")
            .WithDescription("Removes the folder's link, so it belongs where it would on its own. A folder that shares a project by a common root commit (a fork) and has no link is given a project of its own instead. Project skills move as on a link, so an unlink undoes what the link moved; 409 on a name clash, and then nothing changes.")
            .Produces<ProjectLinkResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict);
    }

    private static async Task<IResult> ResolveAsync(ProjectFolderRequest request, ClaimsPrincipal user, ProjectService service, CancellationToken cancellationToken)
    {
        if (!TryGetOwner(user, out var ownerId))
            return Results.Unauthorized();
        if (request.Facts.Problem() is { } problem)
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: problem);

        var view = await service.ShowAsync(ownerId, ProjectIdentity.From(request.MachineId, request.Facts), cancellationToken);
        return Results.Ok(ProjectViewResponse.From(view));
    }

    private static async Task<IResult> LinkAsync(LinkFolderRequest request, ClaimsPrincipal user, ProjectService service, CancellationToken cancellationToken)
    {
        if (!TryGetOwner(user, out var ownerId))
            return Results.Unauthorized();
        if (request.Facts.Problem() is { } problem)
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: problem);
        if (string.IsNullOrWhiteSpace(request.Project))
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Name the project to link to, by id or name.");

        var result = await service.LinkAsync(ownerId, ProjectIdentity.From(request.MachineId, request.Facts), request.Project.Trim(), cancellationToken);
        return ToResult(result);
    }

    private static async Task<IResult> UnlinkAsync(ProjectFolderRequest request, ClaimsPrincipal user, ProjectService service, CancellationToken cancellationToken)
    {
        if (!TryGetOwner(user, out var ownerId))
            return Results.Unauthorized();
        if (request.Facts.Problem() is { } problem)
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: problem);

        var result = await service.UnlinkAsync(ownerId, ProjectIdentity.From(request.MachineId, request.Facts), cancellationToken);
        return ToResult(result);
    }

    private static IResult ToResult(ProjectLinkResult result) => result.Failure switch
    {
        null => Results.Ok(ProjectLinkResponse.From(result)),
        ProjectLinkFailure.SkillNameClash => Results.Problem(statusCode: StatusCodes.Status409Conflict, title: result.Error),
        _ => Results.Problem(statusCode: StatusCodes.Status404NotFound, title: result.Error),
    };

    private static bool TryGetOwner(ClaimsPrincipal user, out Guid ownerId) =>
        Guid.TryParse(user.GetClaim(Claims.Subject), out ownerId);
}

/// <summary>A folder on a machine, described by the client's facts.</summary>
/// <param name="MachineId">The machine the folder is on, the id it backs up sessions under.</param>
/// <param name="Facts">What the client found out about the folder.</param>
public sealed record ProjectFolderRequest(Guid MachineId, ProjectFacts Facts);

/// <summary>A folder to link, and the project to link it to.</summary>
/// <param name="MachineId">The machine the folder is on.</param>
/// <param name="Facts">What the client found out about the folder.</param>
/// <param name="Project">The project's id, or its exact name (ignoring case).</param>
public sealed record LinkFolderRequest(Guid MachineId, ProjectFacts Facts, string Project);

/// <summary>Which project a folder belongs to.</summary>
/// <param name="ProjectId">The project, or <c>null</c> when the first backup from here would create it.</param>
/// <param name="Name">Its name, or the name it would get.</param>
/// <param name="RecognisedBy">`link`, `rootCommit`, `previousRootCommit`, `remote`, `folder`, or `new`.</param>
/// <param name="Folder">The folder it is filed by: the main worktree for git.</param>
/// <param name="Subpath">The path within the repo, in a subfolder.</param>
/// <param name="Suggestions">Projects a folder outside git looks like. Never applied; link one to accept it.</param>
public sealed record ProjectViewResponse(
    Guid? ProjectId, string Name, ProjectRecognition RecognisedBy, string Folder, string? Subpath, IReadOnlyList<ProjectSuggestion> Suggestions)
{
    internal static ProjectViewResponse From(ProjectView v) => new(v.ProjectId, v.Name, v.RecognisedBy, v.Folder, v.Subpath, v.Suggestions);
}

/// <summary>What a link or unlink did, and where the folder belongs now.</summary>
/// <param name="Outcome">`linked`, `unlinked`, `separated` or `nothingToUnlink`.</param>
/// <param name="Project">Where the folder belongs now.</param>
/// <param name="MovedSkills">The names of the project skills that moved with the folder, sorted; empty when none did.</param>
public sealed record ProjectLinkResponse(ProjectLinkOutcome Outcome, ProjectViewResponse Project, IReadOnlyList<string> MovedSkills)
{
    internal static ProjectLinkResponse From(ProjectLinkResult r) => new(r.Outcome, ProjectViewResponse.From(r.View!), r.MovedSkills);
}
