using System.ComponentModel;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using OpenIddict.Abstractions;
using Protostar.Registry.Api.Infrastructure;
using Protostar.Registry.Api.Search;
using Protostar.Registry.Api.Skills;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Protostar.Registry.Api.Sessions;

/// <summary>The machine and transcript API: register machines, back up transcripts chunk by chunk, list and read them.</summary>
/// <remarks>Every endpoint acts on the caller's own data only. The handlers stay thin; <see cref="TranscriptService"/> does the work.</remarks>
public static class SessionEndpoints
{
    private const string AppendDescription =
        $$"""
        Appends the next chunk of a transcript. Transcripts are append-only, so each chunk must start exactly
        where the stored content ends: pass the current `revision` and the `offset` equal to its `length`.
        To start over because the local file was rewritten rather than appended, send `revision` + 1 with
        `offset` 0; earlier revisions are kept.

        The body is the raw bytes, at most 16 MiB uncompressed, and must end with a newline so every chunk
        holds whole lines. Send `Content-Encoding: gzip` to upload it compressed, and `X-Content-SHA256`
        (hex SHA-256 of the uncompressed bytes) to have the registry verify it.

        A `409` means the chunk was not at the current position, usually because an earlier upload partly
        succeeded or another process got there first. Its body carries the position to resume from.

        A `404` with problem `type` `{{SessionProblemTypes.UnknownTranscript}}` means the registry does
        not know the transcript: register it with `PUT` first. A `400` with problem `type`
        `{{SessionProblemTypes.UnknownMachine}}` means `machineId` is not registered: register it with
        `PUT /v1/machines/{machineId}` and retry.
        """;

    private static readonly string LookupDescription =
        $"""
        Answers, for up to {TranscriptService.MaxLookup} transcripts at once, whether the registry holds each one and
        where its stored content ends, so a client can check its backups without one request per transcript.

        The answer has one entry per requested transcript, in request order. A transcript the caller does not own, or
        whose harness or key is not valid, is reported as not `known`, the same as one that was never registered.
        """;

    private const string DownloadDescription =
        """
        Streams the current revision's content, in order.

        By default the content is exactly as stored: the original JSONL bytes, which backups, restores and
        verification rely on. Pass `redacted=true` to read it as a person or an agent should: each line has
        known secrets (tokens, keys, passwords) replaced by a marker such as `[redacted:github-token]`, the
        same redaction search applies. Secrets are redacted inside JSON string values, so every line that was
        JSON is still JSON; a line with nothing to redact is unchanged, byte for byte.
        """;

    private const string UnknownMachineResponse =
        $"Invalid request. When the machine is not registered, the problem `type` is `{SessionProblemTypes.UnknownMachine}`; register it with `PUT /v1/machines/{{machineId}}` and retry.";

    private const string UnknownTranscriptResponse =
        $"No transcript with that harness and key. The problem `type` is `{SessionProblemTypes.UnknownTranscript}`.";

    public static void MapSessionEndpoints(this WebApplication app)
    {
        var v1 = app.MapGroup("/v1").RequireAuthorization(SkillEndpoints.Policy).DisableAntiforgery();

        var machines = v1.MapGroup("/machines").WithTags(ApiTags.Machines);
        machines.MapPut("/{machineId:guid}", RegisterMachineAsync)
            .WithName("RegisterMachine")
            .WithSummary("Register this machine")
            .WithDescription("Registers a machine under an id the client generated, or updates its name and OS. Idempotent, including under concurrency: when several requests register the same new id at once, one gets `201` and the rest get `200`, all naming the one machine.")
            .Produces<MachineResponse>(StatusCodes.Status201Created)
            .Produces<MachineResponse>()
            .WithResponseDescription(StatusCodes.Status201Created, "The machine was registered.")
            .WithResponseDescription(StatusCodes.Status200OK, "The machine was already registered; its details were updated.")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .WithResponseDescription(StatusCodes.Status400BadRequest, "The id is all zeros. Generate a random id once and keep it.");
        machines.MapGet("", ListMachinesAsync)
            .WithName("ListMachines")
            .WithSummary("List my machines")
            .Produces<MachineResponse[]>();

        var transcripts = v1.MapGroup("/transcripts/{harness}/{key}").WithTags(ApiTags.Transcripts);
        transcripts.MapPut("", RegisterTranscriptAsync)
            .WithName("RegisterTranscript")
            .WithSummary("Register a transcript")
            .WithDescription("Registers a transcript before its first chunk, or refreshes where it lives. Returns the position the next chunk must start at.")
            .Produces<TranscriptPositionResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .WithResponseDescription(StatusCodes.Status400BadRequest, UnknownMachineResponse)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithResponseDescription(StatusCodes.Status409Conflict, "Already registered as a different kind (session versus subagent).");
        transcripts.MapPost("/content", AppendAsync)
            .WithName("AppendTranscriptContent")
            .WithSummary("Append a chunk to a transcript")
            .WithDescription(AppendDescription)
            .WithMetadata(new RequestSizeLimitAttribute(Transcript.MaxChunkBytes + (1024 * 1024)))
            .Accepts<byte[]>("application/octet-stream")
            .Produces<TranscriptPositionResponse>()
            .WithResponseDescription(StatusCodes.Status200OK, "The chunk was stored. The body is the new position.")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .WithResponseDescription(StatusCodes.Status400BadRequest, UnknownMachineResponse)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithResponseDescription(StatusCodes.Status404NotFound, UnknownTranscriptResponse)
            .Produces<TranscriptPositionProblem>(StatusCodes.Status409Conflict, "application/problem+json")
            .WithResponseDescription(StatusCodes.Status409Conflict, "The chunk was not at the current position; `revision` and `length` say where to resume.")
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge);
        transcripts.MapGet("/content", DownloadAsync)
            .WithName("DownloadTranscriptContent")
            .WithSummary("Download a transcript")
            .WithDescription(DownloadDescription)
            .Produces<string>(StatusCodes.Status200OK, "application/x-ndjson")
            .WithResponseDescription(StatusCodes.Status200OK, "The transcript's JSONL: as stored, or with secrets redacted when `redacted=true`.")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithResponseDescription(StatusCodes.Status404NotFound, UnknownTranscriptResponse);
        transcripts.MapGet("", GetAsync)
            .WithName("GetTranscript")
            .WithSummary("Get a transcript's summary")
            .WithDescription("Returns what the registry parsed from the transcript, and for a session, its subagents.")
            .Produces<TranscriptDetailResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithResponseDescription(StatusCodes.Status404NotFound, UnknownTranscriptResponse);

        v1.MapPost("/transcripts/lookup", LookupAsync)
            .WithTags(ApiTags.Transcripts)
            .WithName("LookUpTranscripts")
            .WithSummary("Look up many transcripts' positions at once")
            .WithDescription(LookupDescription)
            .Produces<TranscriptLookupResponse[]>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .WithResponseDescription(StatusCodes.Status400BadRequest, $"More than {TranscriptService.MaxLookup} transcripts in one request.");

        v1.MapGet("/transcripts", ListAsync)
            .WithTags(ApiTags.Transcripts)
            .WithName("ListTranscripts")
            .WithSummary("List my transcripts")
            .WithDescription("Lists sessions (or subagents) from every machine, most recently active first. Page with `before`, passing the last item's `lastActivityAt`.")
            .Produces<TranscriptResponse[]>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .WithDateTimeQueryParameters("since", "before");
    }

    private static async Task<IResult> RegisterMachineAsync(
        [Description("The machine's id, generated by the client once and kept.")] Guid machineId,
        RegisterMachineRequest request, ClaimsPrincipal user, TranscriptService service, CancellationToken cancellationToken)
    {
        if (!TryGetOwner(user, out var ownerId))
            return Results.Unauthorized();

        var result = await service.RegisterMachineAsync(ownerId, machineId, request.Name, request.OperatingSystem, cancellationToken);
        if (result.Failure != TranscriptFailure.None)
            return Failure(result);

        var (machine, created) = result.Value;
        var response = MachineResponse.From(machine);
        return created ? Results.Created($"/v1/machines/{machine.Id}", response) : Results.Ok(response);
    }

    private static async Task<IResult> ListMachinesAsync(ClaimsPrincipal user, RegistryDbContext db, CancellationToken cancellationToken)
    {
        if (!TryGetOwner(user, out var ownerId))
            return Results.Unauthorized();

        var machines = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync(
            db.Machines.Where(m => m.OwnerId == ownerId).OrderByDescending(m => m.LastSeenAt), cancellationToken);
        return Results.Ok(machines.Select(MachineResponse.From).ToArray());
    }

    private static async Task<IResult> RegisterTranscriptAsync(
        [Description("The harness that wrote the transcript, for example `claude-code`.")] string harness,
        [Description("The transcript's id within the harness: a session id, or `<session id>.<agent id>` for a subagent.")] string key,
        RegisterTranscriptRequest request, ClaimsPrincipal user, TranscriptService service, CancellationToken cancellationToken)
    {
        if (!TryGetOwner(user, out var ownerId))
            return Results.Unauthorized();

        var result = await service.RegisterTranscriptAsync(ownerId, harness, key, request, cancellationToken);
        return result.Failure == TranscriptFailure.None ? Results.Ok(TranscriptPositionResponse.From(result.Value!)) : Failure(result);
    }

    private static async Task<IResult> AppendAsync(
        [Description("The harness that wrote the transcript.")] string harness,
        [Description("The transcript's key.")] string key,
        [Description("The revision the chunk belongs to: the current one, or the current one + 1 to start over.")] int revision,
        [Description("Where the chunk starts: the current `length`, or 0 when starting a new revision.")] long offset,
        [Description("The registered machine sending the chunk.")] Guid machineId,
        [FromHeader(Name = "Content-Encoding"), Description("`gzip` when the body is compressed.")] string? contentEncoding,
        [FromHeader(Name = "X-Content-SHA256"), Description("Hex SHA-256 of the uncompressed chunk, to verify it arrived intact.")] string? contentSha256,
        HttpRequest request, ClaimsPrincipal user, TranscriptService service, CancellationToken cancellationToken)
    {
        if (!TryGetOwner(user, out var ownerId))
            return Results.Unauthorized();

        var gzip = string.Equals(contentEncoding, "gzip", StringComparison.OrdinalIgnoreCase);
        var result = await service.AppendAsync(ownerId, harness, key, revision, offset, machineId, request.Body, gzip, contentSha256, cancellationToken);
        return result.Failure == TranscriptFailure.None ? Results.Ok(TranscriptPositionResponse.From(result.Value!)) : Failure(result);
    }

    private static async Task<IResult> DownloadAsync(
        [Description("The harness that wrote the transcript.")] string harness,
        [Description("The transcript's key.")] string key,
        [Description("`true` to redact secrets from the content before it is sent; `false` (the default) for the stored bytes.")] bool? redacted,
        ClaimsPrincipal user, TranscriptService service, CancellationToken cancellationToken)
    {
        if (!TryGetOwner(user, out var ownerId))
            return Results.Unauthorized();

        var opened = await service.OpenAsync(ownerId, harness, key, cancellationToken);
        if (opened is not { } found)
            return Failure(TranscriptResult<Transcript>.NotFound());

        return Results.Stream(
            stream => service.CopyContentAsync(found.Chunks, redacted ?? false, stream, cancellationToken),
            "application/x-ndjson",
            fileDownloadName: $"{found.Transcript.Key.Value}.jsonl");
    }

    private static async Task<IResult> GetAsync(
        [Description("The harness that wrote the transcript.")] string harness,
        [Description("The transcript's key.")] string key,
        ClaimsPrincipal user, TranscriptService service, CancellationToken cancellationToken)
    {
        if (!TryGetOwner(user, out var ownerId))
            return Results.Unauthorized();

        var result = await service.GetAsync(ownerId, harness, key, cancellationToken);
        if (result.Failure != TranscriptFailure.None)
            return Failure(result);

        var (view, subagents) = result.Value;
        return Results.Ok(TranscriptDetailResponse.From(view, subagents));
    }

    private static async Task<IResult> LookupAsync(
        TranscriptLookupRequest request, ClaimsPrincipal user, TranscriptService service, CancellationToken cancellationToken)
    {
        if (!TryGetOwner(user, out var ownerId))
            return Results.Unauthorized();

        var result = await service.LookupAsync(ownerId, request.Transcripts ?? [], cancellationToken);
        return result.Failure == TranscriptFailure.None ? Results.Ok(result.Value) : Failure(result);
    }

    private static async Task<IResult> ListAsync(
        ClaimsPrincipal user, TranscriptService service, CancellationToken cancellationToken,
        [Description("`session` (default) or `subagent`.")] TranscriptKind kind = TranscriptKind.Session,
        [Description("Only this harness, for example `claude-code`.")] string? harness = null,
        [Description("Only transcripts from this machine.")] Guid? machine = null,
        [Description("Only sessions in this project: its id, or text its name contains, for example `protostar`.")] string? project = null,
        [Description("Only transcripts active at or after this time, with any offset; one without an offset is UTC.")] string? since = null,
        [Description("Only transcripts last active before this time; pass the previous page's last `lastActivityAt`.")] string? before = null,
        [Description("Page size, 1 to 200.")] int limit = 50)
    {
        if (!TryGetOwner(user, out var ownerId))
            return Results.Unauthorized();
        if (!QueryTimestamp.TryParseUtc(since, out var sinceUtc))
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: QueryTimestamp.InvalidTitle(nameof(since)));
        if (!QueryTimestamp.TryParseUtc(before, out var beforeUtc))
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: QueryTimestamp.InvalidTitle(nameof(before)));

        var views = await service.ListAsync(ownerId, new TranscriptQuery(kind, harness, machine, project, sinceUtc, beforeUtc, limit), cancellationToken);
        return Results.Ok(views.Select(TranscriptResponse.From).ToArray());
    }

    private static IResult Failure<T>(TranscriptResult<T> result) => result.Failure switch
    {
        TranscriptFailure.UnknownMachine => Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: result.Message, type: SessionProblemTypes.UnknownMachine),
        TranscriptFailure.NotFound => Results.Problem(statusCode: StatusCodes.Status404NotFound, title: result.Message, type: SessionProblemTypes.UnknownTranscript),
        TranscriptFailure.Conflict => Results.Problem(statusCode: StatusCodes.Status409Conflict, title: result.Message),
        TranscriptFailure.TooLarge => Results.Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: result.Message),
        TranscriptFailure.Mismatch => Results.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: result.Message,
            extensions: new Dictionary<string, object?>
            {
                ["revision"] = result.Current!.Revision,
                ["length"] = result.Current.Length,
            }),
        _ => Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: result.Message),
    };

    private static bool TryGetOwner(ClaimsPrincipal user, out Guid ownerId) =>
        Guid.TryParse(user.GetClaim(Claims.Subject), out ownerId);
}

/// <summary>Problem <c>type</c> URIs that identify a refusal independently of its title.</summary>
/// <remarks>
/// Clients branch on these, not on the title text, which is for people. The titles stay as they were because
/// released clients match them. The URIs are identifiers and need not resolve.
/// </remarks>
public static class SessionProblemTypes
{
    /// <summary>The request named a machine the caller has not registered (status <c>400</c>).</summary>
    public const string UnknownMachine = "https://protostar.dev/problems/unknown-machine";

    /// <summary>No transcript with that harness and key (status <c>404</c>).</summary>
    public const string UnknownTranscript = "https://protostar.dev/problems/unknown-transcript";
}

/// <summary>A machine's details as sent when registering it.</summary>
/// <param name="Name">A display name, usually the host name.</param>
/// <param name="OperatingSystem">For example <c>linux</c>, <c>windows</c> or <c>macos</c>.</param>
public sealed record RegisterMachineRequest(string Name, string OperatingSystem);

/// <summary>A registered machine.</summary>
/// <param name="Id">The client-generated id.</param>
/// <param name="Name">The display name.</param>
/// <param name="OperatingSystem">The operating system.</param>
/// <param name="RegisteredAt">When it first registered.</param>
/// <param name="LastSeenAt">When it last registered or sent content.</param>
public sealed record MachineResponse(Guid Id, string Name, string OperatingSystem, DateTimeOffset RegisteredAt, DateTimeOffset LastSeenAt)
{
    internal static MachineResponse From(Machine m) => new(m.Id, m.Name, m.OperatingSystem, m.RegisteredAt, m.LastSeenAt);
}

/// <summary>Where a transcript's stored content ends: the position the next chunk must start at.</summary>
/// <param name="Harness">The harness id.</param>
/// <param name="Key">The transcript key.</param>
/// <param name="Revision">The current revision.</param>
/// <param name="Length">Bytes stored for the current revision; the next chunk's offset.</param>
/// <param name="ChunkCount">Chunks stored, across all revisions.</param>
public sealed record TranscriptPositionResponse(string Harness, string Key, int Revision, long Length, int ChunkCount)
{
    internal static TranscriptPositionResponse From(Transcript t) => new(t.Harness.Value, t.Key.Value, t.Revision, t.Length, t.ChunkCount);
}

/// <summary>The transcripts to look up.</summary>
/// <param name="Transcripts">Each one's harness and key; at most <see cref="TranscriptService.MaxLookup"/>.</param>
public sealed record TranscriptLookupRequest(IReadOnlyList<TranscriptReference>? Transcripts);

/// <summary>A transcript named by its harness and key.</summary>
/// <param name="Harness">The harness id, for example <c>claude-code</c>.</param>
/// <param name="Key">The transcript's key within the harness.</param>
public sealed record TranscriptReference(string Harness, string Key);

/// <summary>What the registry holds of one looked-up transcript.</summary>
/// <param name="Harness">The harness id, as requested.</param>
/// <param name="Key">The key, as requested.</param>
/// <param name="Known">Whether the registry holds the transcript; when false, <paramref name="Revision"/> and <paramref name="Length"/> are <c>null</c>.</param>
/// <param name="Revision">The current revision.</param>
/// <param name="Length">Bytes stored for the current revision; the next chunk's offset.</param>
public sealed record TranscriptLookupResponse(string Harness, string Key, bool Known, int? Revision, long? Length);

/// <summary>A <c>409</c> problem for a chunk that was not at the current position.</summary>
/// <param name="Title">What went wrong.</param>
/// <param name="Status">Always 409.</param>
/// <param name="Revision">The current revision.</param>
/// <param name="Length">Where the next chunk must start.</param>
public sealed record TranscriptPositionProblem(string Title, int Status, int Revision, long Length);

/// <summary>A transcript as listings show it: what the registry parsed from it, and where it came from.</summary>
/// <param name="Harness">The harness that wrote it.</param>
/// <param name="Key">Its key within the harness.</param>
/// <param name="Kind">Session or subagent.</param>
/// <param name="ParentKey">For a subagent, the session it ran in.</param>
/// <param name="Title">The harness's title for the session, or the first prompt when it has none; known secrets redacted.</param>
/// <param name="FirstPrompt">The first thing the user asked, shortened; known secrets redacted.</param>
/// <param name="ProjectId">The project the session belongs to, the same on every machine; <c>null</c> until its working directory is known.</param>
/// <param name="Project">The project's display name: its normalised remote, or a folder name.</param>
/// <param name="ProjectSubpath">The working directory's path within its repo, when it is a subfolder.</param>
/// <param name="WorkingDirectory">The working directory on the machine it ran on.</param>
/// <param name="GitBranch">The branch it was on.</param>
/// <param name="HarnessVersion">The harness version that last wrote to it.</param>
/// <param name="MachineId">The machine it came from.</param>
/// <param name="MachineName">That machine's name.</param>
/// <param name="SourcePath">The file's path on that machine.</param>
/// <param name="StartedAt">Its first timestamp.</param>
/// <param name="LastActivityAt">Its latest timestamp.</param>
/// <param name="UserMessages">Prompts the user typed.</param>
/// <param name="AssistantMessages">Replies from the model.</param>
/// <param name="Lines">Lines stored.</param>
/// <param name="Length">Bytes stored for the current revision.</param>
/// <param name="Revision">The current revision.</param>
/// <param name="SubagentCount">Subagent transcripts backed up for this session.</param>
public sealed record TranscriptResponse(
    string Harness, string Key, TranscriptKind Kind, string? ParentKey, string? Title, string? FirstPrompt, Guid? ProjectId,
    string? Project, string? ProjectSubpath, string? WorkingDirectory, string? GitBranch, string? HarnessVersion, Guid MachineId, string? MachineName, string SourcePath,
    DateTimeOffset? StartedAt, DateTimeOffset? LastActivityAt, int UserMessages, int AssistantMessages, int Lines, long Length,
    int Revision, int SubagentCount)
{
    internal static TranscriptResponse From(TranscriptView view)
    {
        var t = view.Transcript;
        var s = t.Summary;
        return new TranscriptResponse(
            t.Harness.Value, t.Key.Value, t.Kind, t.ParentKey?.Value, SecretRedactor.RedactOrNull(s.Title ?? s.FirstPrompt), SecretRedactor.RedactOrNull(s.FirstPrompt), t.ProjectId,
            view.ProjectName, t.ProjectSubpath, s.WorkingDirectory, s.GitBranch, s.HarnessVersion, t.MachineId, view.Machine?.Name, t.SourcePath, s.StartedAt, s.LastActivityAt,
            s.UserMessages, s.AssistantMessages, s.Lines, t.Length, t.Revision, view.SubagentCount);
    }
}

/// <summary>A transcript with its harness metadata and, for a session, its subagents.</summary>
/// <param name="Transcript">The transcript.</param>
/// <param name="Metadata">Harness-specific metadata, such as a Claude Code subagent's type and description.</param>
/// <param name="Subagents">The session's subagent transcripts, earliest first; empty for a subagent.</param>
public sealed record TranscriptDetailResponse(TranscriptResponse Transcript, JsonElement? Metadata, IReadOnlyList<TranscriptResponse> Subagents)
{
    internal static TranscriptDetailResponse From(TranscriptView view, IReadOnlyList<TranscriptView> subagents) => new(
        TranscriptResponse.From(view),
        view.Transcript.MetadataJson is { } json ? JsonDocument.Parse(json).RootElement.Clone() : null,
        subagents.Select(TranscriptResponse.From).ToList());
}
