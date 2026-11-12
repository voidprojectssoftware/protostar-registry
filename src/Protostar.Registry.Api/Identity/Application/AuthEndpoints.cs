using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using Protostar.Registry.Api.Infrastructure;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Protostar.Registry.Api.Identity;

/// <summary>
/// The OAuth2/OIDC endpoints exposed to the CLI. OpenIddict handles protocol mechanics; these
/// handlers bridge the upstream GitHub login to a registry <see cref="User"/> and decide what
/// goes into the issued tokens.
/// </summary>
public static class AuthEndpoints
{
    public const string GitHubScheme = "GitHub";

    // The authorize request's parameters (RFC 6749 section 4.1.1, RFC 7636, OpenID Connect Core 3.1.2.1), plus
    // the registry's identity_provider shortcut. Documented once, for both the query and the form variant.
    private static readonly ProtocolParameter[] AuthorizeParameters =
    [
        new("response_type", true, "Must be `code`.", "code"),
        new("client_id", true, "The registered client, for example `protostar-cli`."),
        new("redirect_uri", true, "Where to send the browser with the code. Must match a URI registered for the client; " +
            "loopback clients may use any port (RFC 8252)."),
        new("scope", false, "Space-separated scopes. Request `openid profile email registry offline_access` to call the API " +
            "and receive a refresh token."),
        new("state", false, "An opaque value returned unchanged on the redirect, to tie the response to the request."),
        new("code_challenge", true, "The PKCE challenge: base64url(SHA-256(code_verifier))."),
        new("code_challenge_method", true, "Must be `S256`.", "S256"),
        new("identity_provider", false, "Skip the sign-in chooser and go straight to this provider, for example `github`. " +
            "Specific to this registry."),
    ];

    private static readonly ProtocolParameter[] LogoutParameters =
    [
        new("id_token_hint", false, "The ID token of the session being ended."),
        new("post_logout_redirect_uri", false, "Where to send the browser afterwards. Must be registered for the client."),
        new("state", false, "An opaque value returned unchanged on that redirect."),
    ];

    private const string AuthorizeDescription =
        """
        Starts sign-in in the user's browser. If the browser has no registry session, it is sent to the sign-in
        chooser (or straight to the provider named by `identity_provider`); after sign-in it comes back here.
        With a session, the browser is redirected to `redirect_uri` with `code` and `state`, and the client
        exchanges the code at the token endpoint.

        A request the registry rejects (unknown client, unregistered redirect URI, unsupported scope, missing PKCE)
        is answered with `400` and a plain-text body of `error`, `error_description` and `error_uri` lines.
        """;

    private const string AuthorizeError =
        "The request was rejected. The body is plain text, one `name:value` line each for `error` (for example " +
        "`invalid_request` or `invalid_scope`), `error_description` and `error_uri`.";

    public static void MapAuthEndpoints(this WebApplication app)
    {
        var connect = app.MapGroup("/connect").WithTags(ApiTags.OAuth);

        connect.MapGet("/authorize", AuthorizeAsync)
            .WithName("Authorize")
            .WithSummary("Start sign-in (authorization code with PKCE)")
            .WithDescription(AuthorizeDescription)
            .WithQueryParameters(AuthorizeParameters)
            .ProducesRedirect("To the sign-in chooser or provider when the browser has no session; otherwise to " +
                "`redirect_uri` with `code` and `state`.")
            .Produces<string>(StatusCodes.Status400BadRequest, "text/plain")
            .WithResponseDescription(StatusCodes.Status400BadRequest, AuthorizeError);

        connect.MapPost("/authorize", AuthorizeAsync)
            .WithName("AuthorizeWithForm")
            .WithSummary("Start sign-in, with the parameters in a form body")
            .WithDescription("The same request as `GET /connect/authorize`, sent as a form post (OpenID Connect Core 3.1.2.1).")
            .WithFormParameters(AuthorizeParameters)
            .ProducesRedirect("As for `GET /connect/authorize`.")
            .Produces<string>(StatusCodes.Status400BadRequest, "text/plain")
            .WithResponseDescription(StatusCodes.Status400BadRequest, AuthorizeError);

        connect.MapPost("/token", ExchangeAsync)
            .WithName("GetToken")
            .WithSummary("Exchange a code, or renew a session")
            .WithDescription(
                """
                With `grant_type=authorization_code`, exchanges the code from the authorize redirect, plus the PKCE
                `code_verifier`, for tokens. With `grant_type=refresh_token`, renews them from a refresh token.
                Each refresh returns a new refresh token; keep the latest one.
                """)
            .Accepts<TokenRequest>("application/x-www-form-urlencoded")
            .Produces<TokenResponse>()
            .WithResponseDescription(StatusCodes.Status200OK, "The tokens.")
            .Produces<OAuthError>(StatusCodes.Status400BadRequest)
            .WithResponseDescription(StatusCodes.Status400BadRequest,
                "The request was rejected: for example `invalid_grant` for a used, expired, or mismatched code or " +
                "refresh token, or `invalid_request` for a missing parameter.");

        connect.MapGet("/userinfo", UserInfoAsync)
            .WithName("GetUserInfo")
            .WithSummary("Get the signed-in user")
            .WithDescription("Returns the registry user the access token was issued to.")
            .WithMetadata(new RequiresAccessTokenAttribute("openid"))
            .Produces<UserInfoResponse>()
            .WithResponseDescription(StatusCodes.Status200OK, "The user.");

        connect.MapPost("/userinfo", UserInfoAsync)
            .WithName("GetUserInfoWithPost")
            .WithSummary("Get the signed-in user, with POST")
            .WithDescription("The same as `GET /connect/userinfo` (OpenID Connect Core 5.3.1).")
            .WithMetadata(new RequiresAccessTokenAttribute("openid"))
            .Produces<UserInfoResponse>()
            .WithResponseDescription(StatusCodes.Status200OK, "The user.");

        connect.MapGet("/logout", LogoutAsync)
            .WithName("Logout")
            .WithSummary("End the browser's registry session")
            .WithDescription(
                "Signs the browser out of the registry. Tokens already issued stay valid until they expire; a client " +
                "signs out locally by deleting them.")
            .WithQueryParameters(LogoutParameters)
            .ProducesRedirect("To `post_logout_redirect_uri` when given and registered, otherwise to the registry root.");

        connect.MapPost("/logout", LogoutAsync)
            .WithName("LogoutWithForm")
            .WithSummary("End the browser's registry session, with a form body")
            .WithDescription("The same as `GET /connect/logout`, sent as a form post.")
            .WithFormParameters(LogoutParameters)
            .ProducesRedirect("As for `GET /connect/logout`.");
    }

    private static OpenIddictRequest GetServerRequest(HttpContext context) =>
        context.Features.Get<OpenIddictServerAspNetCoreFeature>()?.Transaction?.Request
            ?? throw new InvalidOperationException("The OpenID Connect request cannot be retrieved.");

    private static async Task<IResult> AuthorizeAsync(HttpContext context, RegistryDbContext db)
    {
        var request = GetServerRequest(context);

        // The login step is delegated to an external provider and carried back by the cookie. If the
        // user isn't signed in yet, either auto-forward to a provider they explicitly named (the
        // identity_provider hint) or send them to the registry's sign-in chooser to pick one.
        var login = await context.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        if (!login.Succeeded || login.Principal?.Identity?.IsAuthenticated != true)
        {
            var returnUrl = context.Request.PathBase + context.Request.Path + context.Request.QueryString;

            if (AuthProviders.ByHint(context.Request.Query["identity_provider"].ToString()) is { } hinted)
            {
                return Results.Challenge(
                    new AuthenticationProperties { RedirectUri = returnUrl },
                    [hinted.Scheme]);
            }

            return Results.Redirect($"/login?returnUrl={Uri.EscapeDataString(returnUrl)}");
        }

        var user = await FindOrCreateUserAsync(db, login.Principal);

        var identity = new ClaimsIdentity(
            authenticationType: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme,
            nameType: Claims.Name,
            roleType: Claims.Role);

        identity.SetClaim(Claims.Subject, user.Id.ToString())
            .SetClaim(Claims.Name, user.Name ?? user.Login)
            .SetClaim(Claims.PreferredUsername, user.Login)
            .SetClaim("github_login", user.Login);

        identity.SetScopes(request.GetScopes());
        identity.SetDestinations(GetDestinations);

        return Results.SignIn(
            new ClaimsPrincipal(identity),
            new AuthenticationProperties(),
            OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    // The CancellationToken parameter also keeps this handler from matching the bare RequestDelegate
    // overload (which would silently discard the returned IResult).
    private static async Task<IResult> ExchangeAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var request = GetServerRequest(context);

        if (!request.IsAuthorizationCodeGrantType() && !request.IsRefreshTokenGrantType())
        {
            return Results.BadRequest(new OAuthError
            {
                Error = Errors.UnsupportedGrantType,
                ErrorDescription = "The specified grant type is not supported.",
            });
        }

        // Re-materialize the principal stored in the authorization code / refresh token and
        // re-issue tokens from it. Destinations are re-applied so refreshed claims land correctly.
        var result = await context.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        if (!result.Succeeded || result.Principal is null)
        {
            return Results.Forbid(
                authenticationSchemes: [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme],
                properties: new AuthenticationProperties(new Dictionary<string, string?>
                {
                    [OpenIddictServerAspNetCoreConstants.Properties.Error] = Errors.InvalidGrant,
                    [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] =
                        "The token is no longer valid.",
                }));
        }

        var identity = new ClaimsIdentity(
            result.Principal.Claims,
            authenticationType: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme,
            nameType: Claims.Name,
            roleType: Claims.Role);

        identity.SetDestinations(GetDestinations);

        return Results.SignIn(
            new ClaimsPrincipal(identity),
            new AuthenticationProperties(),
            OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    private static async Task<IResult> UserInfoAsync(HttpContext context, RegistryDbContext db)
    {
        var result = await context.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        if (!result.Succeeded || result.Principal is null)
        {
            return Results.Challenge(
                authenticationSchemes: [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme]);
        }

        var subject = result.Principal.GetClaim(Claims.Subject)!;
        if (!Guid.TryParse(subject, out var id) || await db.Users.FindAsync(id) is not { } user)
            return Results.Ok(new UserInfoResponse { Subject = subject });

        return Results.Ok(new UserInfoResponse
        {
            Subject = subject,
            PreferredUsername = user.Login,
            Name = user.Name,
            GitHubLogin = user.Login,
            AvatarUrl = user.AvatarUrl,
        });
    }

    private static async Task<IResult> LogoutAsync(HttpContext context, CancellationToken cancellationToken)
    {
        await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return Results.SignOut(
            new AuthenticationProperties { RedirectUri = "/" },
            [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme]);
    }

    private static async Task<User> FindOrCreateUserAsync(RegistryDbContext db, ClaimsPrincipal principal)
    {
        var gitHubId = principal.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? throw new InvalidOperationException("GitHub did not return a user id.");
        var login = principal.FindFirstValue(ClaimTypes.Name) ?? gitHubId;
        var name = principal.FindFirstValue("urn:github:name");
        var avatar = principal.FindFirstValue("urn:github:avatar");

        var user = await db.Users.FirstOrDefaultAsync(u => u.GitHubId == gitHubId);
        if (user is null)
        {
            user = new User
            {
                Id = Guid.NewGuid(),
                GitHubId = gitHubId,
                Login = login,
                Name = name,
                AvatarUrl = avatar,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            db.Users.Add(user);
        }
        else
        {
            // Keep the profile fresh on each login.
            user.Login = login;
            user.Name = name;
            user.AvatarUrl = avatar;
        }

        await db.SaveChangesAsync();
        return user;
    }

    /// <summary>Decides which token(s) each claim is emitted into.</summary>
    private static IEnumerable<string> GetDestinations(Claim claim)
    {
        switch (claim.Type)
        {
            case Claims.Name or Claims.PreferredUsername:
                yield return Destinations.AccessToken;
                if (claim.Subject!.HasScope(Scopes.Profile))
                    yield return Destinations.IdentityToken;
                yield break;

            case Claims.Subject:
                yield return Destinations.AccessToken;
                yield return Destinations.IdentityToken;
                yield break;

            default:
                yield return Destinations.AccessToken;
                yield break;
        }
    }
}
