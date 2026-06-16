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

    public static void MapAuthEndpoints(this WebApplication app)
    {
        app.MapMethods("/connect/authorize", ["GET", "POST"], AuthorizeAsync);
        app.MapPost("/connect/token", ExchangeAsync);
        app.MapMethods("/connect/userinfo", ["GET", "POST"], UserInfoAsync);
        app.MapMethods("/connect/logout", ["GET", "POST"], LogoutAsync);
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
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "The specified grant type is not supported.");
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

        var subject = result.Principal.GetClaim(Claims.Subject);
        var claims = new Dictionary<string, object?> { [Claims.Subject] = subject };

        if (Guid.TryParse(subject, out var id) && await db.Users.FindAsync(id) is { } user)
        {
            claims[Claims.PreferredUsername] = user.Login;
            claims[Claims.Name] = user.Name;
            claims["github_login"] = user.Login;
            claims["avatar_url"] = user.AvatarUrl;
        }

        return Results.Ok(claims);
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
