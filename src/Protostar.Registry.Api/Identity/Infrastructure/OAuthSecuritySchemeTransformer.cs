using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Protostar.Registry.Api.Identity;

/// <summary>Adds the registry's OAuth 2.0 security scheme to the OpenAPI document, and a security requirement to each operation that needs a token.</summary>
/// <remarks>
/// <para>
/// The authorize and token URLs are built from the request that fetched the document, so they are right
/// whichever host name or address the reader used, and the Scalar UI can run the sign-in flow against them.
/// </para>
/// <para>
/// An operation needs a token when its endpoint carries authorization metadata (<c>RequireAuthorization</c>)
/// or <see cref="RequiresAccessTokenAttribute"/> (for protocol endpoints such as userinfo that validate the
/// token themselves). Only those operations list the requirement; public ones such as <c>/v1/meta</c> do not.
/// </para>
/// </remarks>
internal sealed class OAuthSecuritySchemeTransformer : IOpenApiDocumentTransformer, IOpenApiOperationTransformer
{
    /// <summary>The OpenAPI security-scheme id; must match the name passed to Scalar's auth config.</summary>
    public const string SchemeId = "OAuth2";

    private static readonly Dictionary<string, string> Scopes = new()
    {
        ["openid"] = "Sign in and receive an ID token.",
        ["profile"] = "Read your name and login.",
        ["email"] = "Read your email address.",
        ["registry"] = "Call the registry API (push and read your skills).",
        ["offline_access"] = "Receive a refresh token so the session survives the access token's expiry.",
    };

    private readonly IHttpContextAccessor _http;

    public OAuthSecuritySchemeTransformer(IHttpContextAccessor http) => _http = http;

    /// <summary>Adds the security scheme.</summary>
    public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        var request = _http.HttpContext?.Request;
        var authority = request is null ? string.Empty : $"{request.Scheme}://{request.Host}{request.PathBase}";

        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes[SchemeId] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.OAuth2,
            Description = "Authorization code flow with PKCE (S256). Send the access token as `Authorization: Bearer <token>`.",
            Flows = new OpenApiOAuthFlows
            {
                AuthorizationCode = new OpenApiOAuthFlow
                {
                    AuthorizationUrl = new Uri($"{authority}/connect/authorize", UriKind.RelativeOrAbsolute),
                    TokenUrl = new Uri($"{authority}/connect/token", UriKind.RelativeOrAbsolute),
                    RefreshUrl = new Uri($"{authority}/connect/token", UriKind.RelativeOrAbsolute),
                    Scopes = Scopes,
                },
            },
        };

        return Task.CompletedTask;
    }

    /// <summary>Adds the security requirement, and a <c>401</c> response, to operations that need a token.</summary>
    public Task TransformAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken cancellationToken)
    {
        var metadata = context.Description.ActionDescriptor.EndpointMetadata;
        var requiredScope = metadata.OfType<RequiresAccessTokenAttribute>().FirstOrDefault()?.Scope
            ?? (metadata.OfType<IAuthorizeData>().Any() ? "registry" : null);
        if (requiredScope is null)
        {
            // An explicit empty list says "public" rather than leaving the reader to guess.
            operation.Security = [];
            return Task.CompletedTask;
        }

        operation.Security =
        [
            new OpenApiSecurityRequirement
            {
                [new OpenApiSecuritySchemeReference(SchemeId, context.Document)] = [requiredScope],
            },
        ];

        operation.Responses ??= [];
        // The authentication middleware answers a missing or bad token with an empty 401. An endpoint may also
        // declare its own 401 body (the skill endpoints do, for a token whose user no longer exists); keep it.
        operation.Responses.TryGetValue("401", out var unauthorized);
        var description = $"No access token, or one that is expired, revoked, or missing the `{requiredScope}` scope. No body.";
        if (unauthorized?.Content is { Count: > 0 })
            description += " A problem details body means the token is valid but its user no longer exists.";

        operation.Responses["401"] = new OpenApiResponse { Description = description, Content = unauthorized?.Content };

        return Task.CompletedTask;
    }
}

/// <summary>Marks an endpoint that validates a bearer token itself, so the OpenAPI document lists the requirement.</summary>
/// <param name="scope">The scope the token must carry.</param>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
internal sealed class RequiresAccessTokenAttribute(string scope) : Attribute
{
    /// <summary>The scope the token must carry.</summary>
    public string Scope { get; } = scope;
}
