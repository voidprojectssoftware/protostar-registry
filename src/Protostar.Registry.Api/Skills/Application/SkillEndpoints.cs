using Microsoft.AspNetCore.Http.Features;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Protostar.Registry.Api.Skills;

/// <summary>
/// The skill-push API: a single-skill upload and a bulk upload. Both take a <c>multipart/form-data</c>
/// body with one file part per file (each part's filename is the path relative to the skill directory
/// root). The creator is the authenticated caller; the body never names it. The endpoints stay thin and
/// hand the work to <see cref="SkillPushService"/>.
/// </summary>
public static class SkillEndpoints
{
    /// <summary>The authorization policy these endpoints require: a valid registry access token.</summary>
    public const string Policy = "registry-api";

    // Request-body ceilings, enforced before the body is buffered: a coarse guard in front of the
    // per-skill SkillSizePolicy. The single endpoint holds one skill plus multipart framing; the bulk
    // endpoint holds several (each still bounded per-skill after grouping) and is kept under the default
    // multipart length limit (~128 MB) so the body-size limit is the binding constraint.
    private const long SingleUploadMaxBytes = SkillSizePolicy.MaxVersionBytes + (1L * 1024 * 1024);
    private const long BulkUploadMaxBytes = 100L * 1024 * 1024;

    public static void MapSkillEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/v1/skills").RequireAuthorization(Policy);
        group.MapPost("", PushOneAsync);
        group.MapPost("/bulk", PushManyAsync);
    }

    /// <summary>
    /// Pushes one skill. The file parts are relative to the skill root (so <c>SKILL.md</c> is at the
    /// root); an optional <c>name</c> form field names the skill when its SKILL.md omits a name.
    /// </summary>
    private static async Task<IResult> PushOneAsync(
        HttpContext context, SkillPushService pushService, CancellationToken cancellationToken)
    {
        if (!TryGetCreator(context, out var creatorId))
            return Results.Unauthorized();

        if (!context.Request.HasFormContentType)
            return MultipartExpected();

        var (form, error) = await TryReadFormAsync(context, SingleUploadMaxBytes, cancellationToken);
        if (error is not null)
            return error;

        var uploads = await ReadUploadsAsync(form!.Files, cancellationToken);
        if (uploads.Count == 0)
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "The upload contained no files.");

        var fallbackName = Blank(form["name"].ToString());
        var result = await pushService.PushAsync(creatorId, fallbackName, uploads, cancellationToken);

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
        HttpContext context, SkillPushService pushService, CancellationToken cancellationToken)
    {
        if (!TryGetCreator(context, out var creatorId))
            return Results.Unauthorized();

        if (!context.Request.HasFormContentType)
            return MultipartExpected();

        var (form, error) = await TryReadFormAsync(context, BulkUploadMaxBytes, cancellationToken);
        if (error is not null)
            return error;

        var uploads = await ReadUploadsAsync(form!.Files, cancellationToken);
        if (uploads.Count == 0)
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "The upload contained no files.");

        var skills = GroupBySkillDirectory(uploads);

        var results = new List<SkillPushResult>(skills.Count);
        foreach (var (skillDirectory, files) in skills)
            results.Add(await pushService.PushAsync(creatorId, skillDirectory, files, cancellationToken));

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

    // Caps the request body (before it is read) and reads the multipart form, translating an over-limit
    // body into a clean 413 instead of letting Kestrel's BadHttpRequestException surface as a raw error.
    private static async Task<(IFormCollection? Form, IResult? Error)> TryReadFormAsync(
        HttpContext context, long maxBytes, CancellationToken cancellationToken)
    {
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } bodySize)
            bodySize.MaxRequestBodySize = maxBytes;

        try
        {
            return (await context.Request.ReadFormAsync(cancellationToken), null);
        }
        catch (BadHttpRequestException exception) when (exception.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return (null, Results.Problem(
                statusCode: StatusCodes.Status413PayloadTooLarge,
                title: $"The upload exceeds the maximum request size of {maxBytes} bytes."));
        }
    }

    private static bool TryGetCreator(HttpContext context, out Guid creatorId) =>
        Guid.TryParse(context.User.GetClaim(Claims.Subject), out creatorId);

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
        var status = result.Error switch
        {
            SkillPushError.UnknownCreator => StatusCodes.Status401Unauthorized,
            SkillPushError.NotCreator => StatusCodes.Status403Forbidden,
            SkillPushError.TooLarge => StatusCodes.Status413PayloadTooLarge,
            _ => StatusCodes.Status400BadRequest,
        };

        return Results.Problem(statusCode: status, title: result.Message);
    }

    private static IResult MultipartExpected() =>
        Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Expected a multipart/form-data upload.");

    private static SkillPushResponse ToResponse(SkillPushResult result) => new(
        Status: result.Status.ToString().ToLowerInvariant(),
        Name: result.Name,
        Version: result.Version,
        SkillId: result.SkillId,
        VersionId: result.VersionId,
        FileCount: result.FileCount,
        Message: result.Message);

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}

/// <summary>The JSON shape returned for a single pushed skill (and each entry in a bulk push).</summary>
public sealed record SkillPushResponse(
    string Status, string? Name, int? Version, Guid? SkillId, Guid? VersionId, int FileCount, string? Message);

/// <summary>The JSON shape returned by the bulk endpoint: one <see cref="SkillPushResponse"/> per skill.</summary>
public sealed record SkillBulkPushResponse(IReadOnlyList<SkillPushResponse> Skills);
