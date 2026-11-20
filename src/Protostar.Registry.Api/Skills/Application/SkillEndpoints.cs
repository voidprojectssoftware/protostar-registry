using Protostar.Registry.Api.Infrastructure;
using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Protostar.Registry.Api.Projects;
using Microsoft.AspNetCore.Mvc;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Protostar.Registry.Api.Skills;

/// <summary>
/// The skills API: push (a single-skill upload and a bulk upload), read (list my skills, download a
/// version so another machine can install it), and rename or delete a skill.
/// </summary>
/// <remarks>
/// The pushes take a <c>multipart/form-data</c> body with one file part per file (each part's filename is
/// the path relative to the skill directory root). The creator is the authenticated caller; the body never
/// names it, and reads only ever return the caller's own skills. The endpoints stay thin and hand the work
/// to <see cref="SkillPushService"/> and <see cref="SkillQueryService"/>.
/// The push handlers bind the upload as parameters (<see cref="IFormFileCollection"/> plus a <c>name</c> field)
/// rather than reading the request directly, so the framework describes the multipart body in the OpenAPI
/// document. Antiforgery is disabled because callers authenticate with a bearer token, not a browser form.
/// A skill is global unless it belongs to a project (ADR 0002): a push names the project by sending the
/// project folder's facts in a <c>project</c> field, and reads name it by <c>projectId</c>. Without either,
/// every endpoint behaves exactly as it did before project skills, so older clients keep working on global
/// skills.
/// </remarks>
public static class SkillEndpoints
{
    /// <summary>The authorization policy these endpoints require: a valid registry access token.</summary>
    public const string Policy = "registry-api";

    // Coarse request-body ceilings, set as endpoint metadata so Kestrel rejects an over-limit body before
    // buffering: a guard in front of the per-skill SkillSizePolicy. The single upload holds one skill plus
    // multipart framing; the bulk upload holds several and is raised above Kestrel's 30 MB default. Both
    // stay under the ~128 MB multipart length limit, so this body-size limit is the binding constraint.
    private const long SingleUploadMaxBytes = SkillSizePolicy.MaxVersionBytes + (1L * 1024 * 1024);
    private const long BulkUploadMaxBytes = 100L * 1024 * 1024;

    public static void MapSkillEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/v1/skills")
            .RequireAuthorization(Policy)
            .DisableAntiforgery()
            .WithTags(ApiTags.Skills);

        group.MapPost("", PushOneAsync)
            .WithName("PushSkill")
            .WithSummary("Push one skill")
            .WithDescription(
                "Uploads one skill as multipart/form-data: one file part per file, where each part's " +
                "filename is the path relative to the skill root (so SKILL.md sits at the root). An optional " +
                "`name` field names the skill when its SKILL.md omits one. An optional `project` field makes it " +
                "a project skill: JSON `{\"machineId\": ..., \"facts\": {...}}`, the same folder description " +
                "`POST /v1/projects/resolve` takes, resolved to a project the way sessions are filed. Without it " +
                "the skill is global. A skill is identified by creator, project and name, so a global skill and " +
                "a project skill of the same name never share versions. The creator is the authenticated " +
                "caller. Returns 201 when a new version is stored, or 200 when the push matches the current " +
                "version byte-for-byte. An optional `baseVersion` field names the version the pushed copy was " +
                "last synced from (0 when it never synced one). When it is given and is not the current version, " +
                "and the content differs from the current version, nothing is stored and the answer is 409 with " +
                $"problem `type` `{SkillProblemTypes.StaleVersion}` and the current version in `currentVersion`: " +
                "the skill changed elsewhere since this copy was synced. A push without `baseVersion` is accepted " +
                "on top of whatever is current, as before. A skill with no version yet accepts any base.")
            .WithMetadata(new RequestSizeLimitAttribute(SingleUploadMaxBytes))
            .Produces<SkillPushResponse>(StatusCodes.Status201Created)
            .Produces<SkillPushResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge);

        group.MapPost("/bulk", PushManyAsync)
            .WithName("PushSkills")
            .WithSummary("Push many skills")
            .WithDescription(
                "Uploads several skills at once as multipart/form-data. Each file part's filename is prefixed " +
                "with its skill's directory segment (`<skill>/<relative path>`); files are grouped by that " +
                "first segment, which also names the skill when its SKILL.md omits one. Every skill is pushed " +
                "as a global skill, with no base version. Returns one result per skill; one bad skill does not fail the others.")
            .WithMetadata(new RequestSizeLimitAttribute(BulkUploadMaxBytes))
            .Produces<SkillBulkPushResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge);

        group.MapGet("", ListAsync)
            .WithName("ListSkills")
            .WithSummary("List my skills")
            .WithDescription(
                "Lists the caller's global skills, each described by its current version: name, project, " +
                "description, version number, content hash, push time, file count, and total size. Sorted " +
                "by name. `includeProjects=true` adds every project's skills; `projectId` lists only that " +
                "project's. A global skill has no `projectId`. The content hash is SHA-256 over each file's `<path>\\n<UPPERCASE hex sha256>\\n`, " +
                "files ordered by path (ordinal), as uppercase hex, so a client can tell whether its local " +
                "copy matches without downloading it.")
            .Produces<SkillListResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        group.MapGet("/{name}/files", GetFilesAsync)
            .WithName("GetSkillFiles")
            .WithSummary("Download a skill version")
            .WithDescription(
                "Returns one version of the caller's skill with every file's bytes (base64 in JSON). " +
                "Omit `projectId` for the global skill of that name, and `version` for the current version. Paths are relative to the skill root with `/` " +
                "separators and never absolute or containing `..`.")
            .Produces<SkillFilesResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/{name}/rename", RenameAsync)
            .WithName("RenameSkill")
            .WithSummary("Rename a skill")
            .WithDescription(
                "Renames the caller's skill, keeping its id and every version: nothing is copied. Omit `projectId` " +
                "for the global skill of that name. The body is `{\"newName\": \"...\"}`; the new name must not be " +
                "blank or contain `/` or `\\`. 409 when the caller already has a skill of that name in the same " +
                "project (or among global skills); then nothing changes. Renaming to the current name is a no-op.")
            .Produces<SkillRenameResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapDelete("/{name}", DeleteAsync)
            .WithName("DeleteSkill")
            .WithSummary("Delete a skill")
            .WithDescription(
                "Deletes the caller's skill with every version and file it holds. Unrecoverable. Omit `projectId` " +
                "for the global skill of that name. Returns 204.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    /// <summary>Renames the caller's skill.</summary>
    private static async Task<IResult> RenameAsync(
        string name,
        [FromQuery] Guid? projectId,
        SkillRenameRequest request,
        ClaimsPrincipal user,
        SkillChangeService changes,
        CancellationToken cancellationToken)
    {
        if (!TryGetCreator(user, out var creatorId))
            return Results.Unauthorized();

        var result = await changes.RenameAsync(creatorId, projectId, name, request.NewName ?? "", cancellationToken);
        return result.Error is null ? Results.Ok(new SkillRenameResponse(name, result.Name!, projectId)) : ChangeFailure(result);
    }

    /// <summary>Deletes the caller's skill and its versions.</summary>
    private static async Task<IResult> DeleteAsync(
        string name,
        [FromQuery] Guid? projectId,
        ClaimsPrincipal user,
        SkillChangeService changes,
        CancellationToken cancellationToken)
    {
        if (!TryGetCreator(user, out var creatorId))
            return Results.Unauthorized();

        var result = await changes.DeleteAsync(creatorId, projectId, name, cancellationToken);
        return result.Error is null ? Results.NoContent() : ChangeFailure(result);
    }

    private static IResult ChangeFailure(SkillChangeResult result) => Results.Problem(
        statusCode: result.Error switch
        {
            SkillChangeError.NotFound => StatusCodes.Status404NotFound,
            SkillChangeError.NameTaken => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status400BadRequest,
        },
        title: result.Message);

    /// <summary>Lists the caller's skills.</summary>
    private static async Task<IResult> ListAsync(
        [FromQuery] bool? includeProjects,
        [FromQuery] Guid? projectId,
        ClaimsPrincipal user,
        SkillQueryService queries,
        CancellationToken cancellationToken)
    {
        if (!TryGetCreator(user, out var creatorId))
            return Results.Unauthorized();

        var skills = await queries.ListAsync(creatorId, new SkillListFilter(includeProjects ?? false, projectId), cancellationToken);
        return Results.Ok(new SkillListResponse(skills.Select(s => new SkillListItem(
            s.Name, s.Description, s.Version, s.ContentHash, s.UpdatedAt, s.FileCount, s.TotalBytes, s.ProjectId, s.ProjectName)).ToArray()));
    }

    /// <summary>Returns one version of the caller's skill, files included.</summary>
    private static async Task<IResult> GetFilesAsync(
        string name,
        [FromQuery] int? version,
        [FromQuery] Guid? projectId,
        ClaimsPrincipal user,
        SkillQueryService queries,
        CancellationToken cancellationToken)
    {
        if (!TryGetCreator(user, out var creatorId))
            return Results.Unauthorized();

        var content = await queries.GetContentAsync(creatorId, projectId, name, version, cancellationToken);
        if (content is null)
        {
            var skill = projectId is { } p ? $"skill '{name}' in project {p}" : $"global skill '{name}'";
            var what = version is { } v ? $"version {v} of {skill}" : skill;
            return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: $"You have no {what}.");
        }

        return Results.Ok(new SkillFilesResponse(
            content.Name,
            content.ProjectId,
            content.Version,
            content.Description,
            content.ContentHash,
            content.PushedAt,
            content.Files.Select(f => new SkillFileResponse(f.Path, f.Size, f.Sha256, f.Content)).ToArray()));
    }

    /// <summary>
    /// Pushes one skill. The file parts are relative to the skill root (so <c>SKILL.md</c> is at the
    /// root); an optional <c>name</c> form field names the skill when its SKILL.md omits a name, and an
    /// optional <c>project</c> form field (a JSON <see cref="ProjectFolderRequest"/>) makes it a project skill.
    /// </summary>
    private static async Task<IResult> PushOneAsync(
        IFormFileCollection files,
        [FromForm] string? name,
        [FromForm] string? project,
        [FromForm] string? baseVersion,
        ClaimsPrincipal user,
        SkillPushService pushService,
        CancellationToken cancellationToken)
    {
        if (!TryGetCreator(user, out var creatorId))
            return Results.Unauthorized();

        // Bound as text and parsed here: a malformed number bound as int? fails inside the framework as an unhandled
        // exception (an Error log for a client mistake) instead of this 400.
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

        var uploads = await ReadUploadsAsync(files, cancellationToken);
        if (uploads.Count == 0)
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "The upload contained no files.");

        var result = await pushService.PushAsync(creatorId, folder, Blank(name), uploads, based, cancellationToken);

        return result.Status switch
        {
            SkillPushStatus.Created => Results.Created($"/v1/skills/{Uri.EscapeDataString(result.Name!)}", ToResponse(result)),
            SkillPushStatus.Unchanged => Results.Ok(ToResponse(result)),
            _ => Failure(result),
        };
    }

    /// <summary>
    /// Pushes many skills at once. Each file part's path is prefixed with its skill's directory segment
    /// (<c>&lt;skill&gt;/&lt;relative path&gt;</c>); files are grouped by that first segment, which also
    /// names the skill when its SKILL.md omits a name. Returns a per-skill result; one bad skill does not
    /// fail the others.
    /// </summary>
    private static async Task<IResult> PushManyAsync(
        IFormFileCollection files,
        ClaimsPrincipal user,
        SkillPushService pushService,
        CancellationToken cancellationToken)
    {
        if (!TryGetCreator(user, out var creatorId))
            return Results.Unauthorized();

        var uploads = await ReadUploadsAsync(files, cancellationToken);
        if (uploads.Count == 0)
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "The upload contained no files.");

        var skills = GroupBySkillDirectory(uploads);

        var results = new List<SkillPushResult>(skills.Count);
        foreach (var (skillDirectory, skillFiles) in skills)
            results.Add(await pushService.PushAsync(creatorId, null, skillDirectory, skillFiles, baseVersion: null, cancellationToken));

        return Results.Ok(new SkillBulkPushResponse(results.Select(ToResponse).ToArray()));
    }

    // Splits a flat bulk upload into one bucket per skill, keyed by each file's first path segment (the
    // skill's directory). The segment is stripped so each bucket's paths are relative to that skill's root,
    // exactly what a single-skill push expects.
    private static Dictionary<string, List<SkillFileUpload>> GroupBySkillDirectory(IEnumerable<SkillFileUpload> uploads)
    {
        var skills = new Dictionary<string, List<SkillFileUpload>>(StringComparer.Ordinal);
        foreach (var upload in uploads)
        {
            var path = upload.RelativePath.Replace('\\', '/').TrimStart('/');
            var slash = path.IndexOf('/');
            var skillDirectory = slash < 0 ? path : path[..slash];
            var pathWithinSkill = slash < 0 ? path : path[(slash + 1)..];

            if (!skills.TryGetValue(skillDirectory, out var files))
                skills[skillDirectory] = files = [];

            files.Add(upload with { RelativePath = pathWithinSkill });
        }

        return skills;
    }

    private static readonly JsonSerializerOptions ProjectJson = new(JsonSerializerDefaults.Web);

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

    private static async Task<List<SkillFileUpload>> ReadUploadsAsync(
        IFormFileCollection files, CancellationToken cancellationToken)
    {
        var uploads = new List<SkillFileUpload>(files.Count);
        foreach (var file in files)
        {
            // The relative path travels as the part's filename; fall back to the field name.
            var path = !string.IsNullOrWhiteSpace(file.FileName) ? file.FileName : file.Name;

            using var buffer = new MemoryStream();
            await using var stream = file.OpenReadStream();
            await stream.CopyToAsync(buffer, cancellationToken);

            uploads.Add(new SkillFileUpload(path, buffer.ToArray()));
        }

        return uploads;
    }

    private static IResult Failure(SkillPushResult result)
    {
        if (result.Error == SkillPushError.StaleVersion)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: result.Message,
                type: SkillProblemTypes.StaleVersion,
                extensions: new Dictionary<string, object?>
                {
                    ["currentVersion"] = result.CurrentVersion,
                    ["projectId"] = result.Project?.ProjectId,
                });
        }

        var status = result.Error switch
        {
            SkillPushError.UnknownCreator => StatusCodes.Status401Unauthorized,
            SkillPushError.NotCreator => StatusCodes.Status403Forbidden,
            SkillPushError.TooLarge => StatusCodes.Status413PayloadTooLarge,
            _ => StatusCodes.Status400BadRequest,
        };

        return Results.Problem(statusCode: status, title: result.Message);
    }

    private static SkillPushResponse ToResponse(SkillPushResult result) => new(
        Status: result.Status.ToString().ToLowerInvariant(),
        Name: result.Name,
        Version: result.Version,
        SkillId: result.SkillId,
        VersionId: result.VersionId,
        FileCount: result.FileCount,
        Message: result.Message,
        ProjectId: result.Project?.ProjectId,
        ProjectName: result.Project?.Name);

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}

/// <summary>Problem <c>type</c> URIs that identify a skills refusal independently of its title.</summary>
/// <remarks>As <see cref="Sessions.SessionProblemTypes"/>: clients branch on these, never on the title. The URIs need not resolve.</remarks>
public static class SkillProblemTypes
{
    /// <summary>
    /// A push named a base version that is not the skill's current version (status <c>409</c>). The problem carries
    /// <c>currentVersion</c>, and <c>projectId</c> for a project skill.
    /// </summary>
    public const string StaleVersion = "https://protostar.dev/problems/stale-skill-version";
}

/// <summary>The JSON shape returned for a single pushed skill (and each entry in a bulk push).</summary>
/// <remarks><c>ProjectId</c> and <c>ProjectName</c> say which project a project skill was filed under; both are null for a global skill.</remarks>
public sealed record SkillPushResponse(
    string Status, string? Name, int? Version, Guid? SkillId, Guid? VersionId, int FileCount, string? Message,
    Guid? ProjectId = null, string? ProjectName = null);

/// <summary>The JSON shape returned by the bulk endpoint: one <see cref="SkillPushResponse"/> per skill.</summary>
public sealed record SkillBulkPushResponse(IReadOnlyList<SkillPushResponse> Skills);

/// <summary>The JSON shape returned by the list endpoint.</summary>
public sealed record SkillListResponse(IReadOnlyList<SkillListItem> Skills);

/// <summary>One skill in <see cref="SkillListResponse"/>, described by its current version.</summary>
/// <remarks><c>ProjectId</c> and <c>ProjectName</c> are null for a global skill.</remarks>
public sealed record SkillListItem(
    string Name, string? Description, int Version, string ContentHash, DateTimeOffset UpdatedAt, int FileCount, long TotalBytes,
    Guid? ProjectId, string? ProjectName);

/// <summary>The JSON body of a rename.</summary>
public sealed record SkillRenameRequest(string? NewName);

/// <summary>The JSON shape returned by a rename: the skill's name before and after, and its project.</summary>
/// <remarks><c>ProjectId</c> is null for a global skill. <c>Name</c> equals <c>OldName</c> when the rename was a no-op.</remarks>
public sealed record SkillRenameResponse(string OldName, string Name, Guid? ProjectId);

/// <summary>The JSON shape returned when downloading a skill version.</summary>
/// <remarks><c>ProjectId</c> is null for a global skill.</remarks>
public sealed record SkillFilesResponse(
    string Name, Guid? ProjectId, int Version, string? Description, string ContentHash, DateTimeOffset PushedAt, IReadOnlyList<SkillFileResponse> Files);

/// <summary>One file in <see cref="SkillFilesResponse"/>; <c>Content</c> is base64 in JSON.</summary>
public sealed record SkillFileResponse(string Path, long Size, string Sha256, byte[] Content);
