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
                The registry that protostar clients back up skills to, and later sessions and memory.

                ## Authentication

                The registry is its own OAuth 2.0 and OpenID Connect provider. Sign-in is delegated to an external
                identity provider (GitHub), so the registry never sees a password. Clients use the authorization code
                flow with PKCE (S256) and send the resulting access token as `Authorization: Bearer <token>`.
                The provider's metadata is at [`/.well-known/openid-configuration`](/.well-known/openid-configuration).

                Endpoints that need a token list it under **security** and answer `401` without one.

                ## Compatibility

                Versioned endpoints live under `/v1`. `GET /v1/meta` lists the API majors this build serves; clients
                check it before anything else and stop with an upgrade hint when theirs is missing.

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
    public const string OAuth = "OAuth and OpenID Connect";
    public const string SignIn = "Sign-in pages";
}
