using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Protostar.Registry.Api.Identity;

/// <summary>
/// Adds the registry's OAuth2 (authorization code + PKCE) security scheme to the OpenAPI document so the
/// Scalar UI can run the login flow and call protected endpoints with a bearer token. Registered only in
/// Development (it points at the pinned local Aspire HTTPS port and pairs with the dev-only Scalar client).
/// </summary>
internal sealed class OAuthSecuritySchemeTransformer : IOpenApiDocumentTransformer
{
    /// <summary>The OpenAPI security-scheme id; must match the name passed to Scalar's auth config.</summary>
    public const string SchemeId = "OAuth2";

    // The API's pinned local Aspire HTTPS port (see AppHost). Safe to hardcode: this transformer runs
    // only in Development.
    private const string Authority = "https://localhost:7443";

    public Task TransformAsync(
        OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        var scopes = new Dictionary<string, string>
        {
            ["openid"] = "Sign in",
            ["profile"] = "Basic profile",
            ["email"] = "Email address",
            ["registry"] = "Access the registry API",
        };

        var scheme = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.OAuth2,
            Flows = new OpenApiOAuthFlows
            {
                AuthorizationCode = new OpenApiOAuthFlow
                {
                    AuthorizationUrl = new Uri($"{Authority}/connect/authorize"),
                    TokenUrl = new Uri($"{Authority}/connect/token"),
                    Scopes = scopes,
                },
            },
        };

        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes[SchemeId] = scheme;

        // Advertise the scheme document-wide so Scalar offers the "Authorize" flow. Public endpoints simply
        // ignore a presented token; the skill-push endpoints enforce it server-side via the auth policy.
        document.Security =
        [
            new OpenApiSecurityRequirement
            {
                [new OpenApiSecuritySchemeReference(SchemeId, document)] = [.. scopes.Keys],
            },
        ];

        return Task.CompletedTask;
    }
}
