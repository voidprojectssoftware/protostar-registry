using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Protostar.Registry.Api.Infrastructure;

/// <summary>Gives the OpenAPI document its title, real version, overview, and described tags.</summary>
/// <remarks>
/// Without this the document is titled after the assembly, versioned 1.0.0, and its tags carry no
/// descriptions. Tag names here must match the names endpoints pass to <c>WithTags</c>, listed in
/// <see cref="ApiTags"/>.
/// </remarks>
internal sealed class RegistryDocumentTransformer : IOpenApiDocumentTransformer
{
    public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        document.Info = new OpenApiInfo
        {
            Title = "Protostar Registry API",
            Version = ApiInfo.Version,
            Description =
                """
                The registry that protostar clients back up skills, session transcripts and agent memory to, from every machine.

                ## Authentication

                The registry is its own OAuth 2.0 and OpenID Connect provider. Sign-in is delegated to an external
                identity provider (GitHub), so the registry never sees a password. Clients use the authorization code
                flow with PKCE (S256) and send the resulting access token as `Authorization: Bearer <token>`.
                The provider's metadata is at [`/.well-known/openid-configuration`](/.well-known/openid-configuration).

                Endpoints that need a token list it under **security** and answer `401` without one.

                ## Compatibility

                Versioned endpoints live under `/v1`. `GET /v1/meta` lists the API majors this build serves; clients
                check it before anything else and stop with an upgrade hint when theirs is missing. Its `capabilities` names
                the features added within the major that this build serves (for example `memory` or `search`); a client
                checks for a name before using its feature, so an older registry is met with an upgrade hint rather than
                a 404. A name is never removed within v1.

                ## Errors

                API endpoints return [RFC 9457](https://www.rfc-editor.org/rfc/rfc9457) problem details
                (`application/problem+json`). The token endpoint returns OAuth error objects
                ([RFC 6749 section 5.2](https://www.rfc-editor.org/rfc/rfc6749#section-5.2)); the authorize
                endpoint returns the same fields as plain-text `name:value` lines, since its caller is a browser.
                """,
        };

        // The generated server URL is the request's base with a trailing slash; paths already start with one.
        foreach (var server in document.Servers ?? [])
            server.Url = server.Url?.TrimEnd('/');

        document.Tags = new HashSet<OpenApiTag>
        {
            new() { Name = ApiTags.Meta, Description = "Service identity and the API compatibility contract." },
            new() { Name = ApiTags.Skills, Description = "Back up skills. Each push stores a new immutable version unless the content matches the current one." },
            new() { Name = ApiTags.Agents, Description = "Back up custom agents (one Markdown file each), versioned like skills." },
            new() { Name = ApiTags.Machines, Description = "The computers and VMs you back up from. Each registers once under an id it generates and keeps." },
            new()
            {
                Name = ApiTags.Transcripts,
                Description = "Harness transcripts (sessions and their subagents), backed up chunk by chunk as they grow, and the " +
                    "summaries the registry parses from them.",
            },
            new()
            {
                Name = ApiTags.Projects,
                Description = "Which project a folder belongs to, the same on every machine (ADR 0002), and linking a folder to a " +
                    "project or unlinking it.",
            },
            new()
            {
                Name = ApiTags.Memory,
                Description = "Agent memory backed up per project from every machine. Each file keeps every distinct content as a " +
                    "version, and a file deleted on a machine is only marked deleted there.",
            },
            new()
            {
                Name = ApiTags.Search,
                Description = "Find sessions and memory by what they say, across every machine and project. Known secrets are " +
                    "redacted before anything is indexed.",
            },
            new()
            {
                Name = ApiTags.OAuth,
                Description = "OAuth 2.0 and OpenID Connect protocol endpoints. Clients normally reach these through an OIDC library " +
                    "configured from the discovery document, not by hand.",
            },
            new()
            {
                Name = ApiTags.SignIn,
                Description = "Browser pages for choosing an identity provider during sign-in. Not called by API clients.",
            },
        };

        return Task.CompletedTask;
    }
}

/// <summary>The OpenAPI tag names endpoints are grouped under.</summary>
internal static class ApiTags
{
    public const string Meta = "Meta";
    public const string Skills = "Skills";
    public const string Agents = "Agents";
    public const string Machines = "Machines";
    public const string Transcripts = "Transcripts";
    public const string Projects = "Projects";
    public const string Memory = "Memory";
    public const string Search = "Search";
    public const string OAuth = "OAuth and OpenID Connect";
    public const string SignIn = "Sign-in pages";
}
