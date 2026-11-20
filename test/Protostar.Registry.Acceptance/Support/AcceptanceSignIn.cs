using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace Protostar.Registry.Acceptance.Support;

/// <summary>A test-only stand-in for the GitHub sign-in: <c>GET /acceptance/sign-in?login=alice</c> signs the browser in as that user.</summary>
/// <remarks>
/// <para>
/// It replaces only the upstream login, as the Development sign-in does: it puts the same claims the GitHub handler
/// produces into the registry's sign-in cookie, and everything after it (<c>/connect/authorize</c> with PKCE, user
/// creation, token issue, bearer validation, the <c>registry</c> scope policy) is the registry's own code, unchanged.
/// </para>
/// <para>
/// It exists in this test assembly only, added to the in-process host through <c>ConfigureTestServices</c>. The API
/// itself has no such path, and the Development sign-in stays Development-only (registry#61): the host here runs as
/// <c>Production</c>, where that sign-in is not registered. Unlike the Development sign-in, it can sign in any number
/// of distinct users, which the owner-scoping scenarios need.
/// </para>
/// </remarks>
public sealed class AcceptanceSignIn : IStartupFilter
{
    public const string Path = "/acceptance/sign-in";

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        // Use rather than Map: Map moves the path into PathBase, and the cookie would then be scoped to this path.
        app.Use(async (context, nextMiddleware) =>
        {
            if (context.Request.Path != Path)
            {
                await nextMiddleware(context);
                return;
            }

            var login = context.Request.Query["login"].ToString();
            var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, "acceptance-" + login),
                new Claim(ClaimTypes.Name, login),
            ], "Acceptance");
            await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
        });

        next(app);
    };
}
