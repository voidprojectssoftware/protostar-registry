using System.Text.Json.Serialization;

namespace Protostar.Registry.Api.Identity;

/// <summary>The form body of a token request (RFC 6749 section 4.1.3 and section 6).</summary>
/// <remarks>
/// OpenIddict reads and validates the request itself, so this type exists only to describe the wire
/// shape in the OpenAPI document. Field names are the protocol's snake_case names.
/// </remarks>
public sealed record TokenRequest
{
    /// <summary>Either <c>authorization_code</c> (exchange a code) or <c>refresh_token</c> (renew a session).</summary>
    [JsonPropertyName("grant_type")]
    public required string GrantType { get; init; }

    /// <summary>The public client's id, for example <c>protostar-cli</c>.</summary>
    [JsonPropertyName("client_id")]
    public required string ClientId { get; init; }

    /// <summary>The authorization code from the authorize redirect. Required for <c>authorization_code</c>.</summary>
    [JsonPropertyName("code")]
    public string? Code { get; init; }

    /// <summary>The redirect URI sent to the authorize endpoint, repeated exactly. Required for <c>authorization_code</c>.</summary>
    [JsonPropertyName("redirect_uri")]
    public string? RedirectUri { get; init; }

    /// <summary>The PKCE verifier whose S256 hash was sent as <c>code_challenge</c>. Required for <c>authorization_code</c>.</summary>
    [JsonPropertyName("code_verifier")]
    public string? CodeVerifier { get; init; }

    /// <summary>The refresh token from an earlier token response. Required for <c>refresh_token</c>.</summary>
    [JsonPropertyName("refresh_token")]
    public string? RefreshToken { get; init; }
}

/// <summary>A successful token response (RFC 6749 section 5.1).</summary>
public sealed record TokenResponse
{
    /// <summary>The bearer token to send as <c>Authorization: Bearer ...</c> on registry API calls. A JWT.</summary>
    [JsonPropertyName("access_token")]
    public required string AccessToken { get; init; }

    /// <summary>Always <c>Bearer</c>.</summary>
    [JsonPropertyName("token_type")]
    public required string TokenType { get; init; }

    /// <summary>Seconds until <see cref="AccessToken"/> expires.</summary>
    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; init; }

    /// <summary>Space-separated scopes granted, for example <c>openid profile email registry offline_access</c>.</summary>
    [JsonPropertyName("scope")]
    public string? Scope { get; init; }

    /// <summary>A token for renewing the session without signing in again. Present when <c>offline_access</c> was granted.</summary>
    [JsonPropertyName("refresh_token")]
    public string? RefreshToken { get; init; }

    /// <summary>An OpenID Connect ID token describing the signed-in user. Present when <c>openid</c> was granted.</summary>
    [JsonPropertyName("id_token")]
    public string? IdToken { get; init; }
}

/// <summary>An OAuth 2.0 error response (RFC 6749 section 5.2).</summary>
public sealed record OAuthError
{
    /// <summary>The error code, for example <c>invalid_grant</c>, <c>invalid_request</c> or <c>unsupported_grant_type</c>.</summary>
    [JsonPropertyName("error")]
    public required string Error { get; init; }

    /// <summary>A human-readable explanation.</summary>
    [JsonPropertyName("error_description")]
    public string? ErrorDescription { get; init; }

    /// <summary>A link to more information about the error.</summary>
    [JsonPropertyName("error_uri")]
    public string? ErrorUri { get; init; }
}

/// <summary>Claims about the signed-in registry user (OpenID Connect Core section 5.3.2).</summary>
public sealed record UserInfoResponse
{
    /// <summary>The registry user's id. Stable for the life of the account; the creator id on everything they push.</summary>
    [JsonPropertyName("sub")]
    public required string Subject { get; init; }

    /// <summary>The user's login at their identity provider, for example their GitHub username.</summary>
    [JsonPropertyName("preferred_username")]
    public string? PreferredUsername { get; init; }

    /// <summary>The user's display name, when the identity provider has one.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>The user's GitHub login. Same as <see cref="PreferredUsername"/>; kept for older clients.</summary>
    [JsonPropertyName("github_login")]
    public string? GitHubLogin { get; init; }

    /// <summary>A URL of the user's avatar image, when the identity provider has one.</summary>
    [JsonPropertyName("avatar_url")]
    public string? AvatarUrl { get; init; }
}
