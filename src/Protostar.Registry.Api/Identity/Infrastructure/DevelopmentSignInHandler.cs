using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Options;

namespace Protostar.Registry.Api.Identity;

/// <summary>
/// A Development-only sign-in provider that signs the browser in as one fixed local user, with no
/// upstream identity provider.
/// </summary>
/// <remarks>
/// It stands in for the GitHub leg only: the rest of the flow (the chooser, <c>/connect/authorize</c>,
/// PKCE, the CLI's loopback callback, token issue and refresh, user creation) runs exactly as it does
/// with GitHub. That lets the stack be exercised end to end on a machine without GitHub OAuth app
/// credentials, by people and by agents. It is registered only when the host environment is
/// Development; see Program.cs.
/// </remarks>
public sealed class DevelopmentSignInHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    /// <summary>The authentication scheme name.</summary>
    public const string SchemeName = "DevLocal";

    /// <summary>The stable external id of the local user, stored where a GitHub id would be.</summary>
    public const string UserId = "dev-local";

    /// <summary>The local user's login.</summary>
    public const string Login = "dev";

    public DevelopmentSignInHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    /// <inheritdoc />
    /// <remarks>This scheme never authenticates a request on its own; the cookie carries the sign-in.</remarks>
    protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(AuthenticateResult.NoResult());

    /// <summary>Signs the local user into the cookie and returns to the challenge's redirect URI.</summary>
    /// <remarks>The claims mirror what the GitHub handler produces, so user creation treats both the same.</remarks>
    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, UserId),
            new Claim(ClaimTypes.Name, Login),
            new Claim("urn:github:name", "Local dev user"),
        ], SchemeName);

        await Context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
        Context.Response.Redirect(LoginPageEndpoints.LocalReturnUrl(properties.RedirectUri));
    }
}
