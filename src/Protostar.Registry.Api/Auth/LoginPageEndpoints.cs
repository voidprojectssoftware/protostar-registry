using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;

namespace Protostar.Registry.Api.Auth;

/// <summary>
/// The registry's own sign-in chooser. Rather than slamming the user straight into one provider,
/// <c>/connect/authorize</c> sends unauthenticated users here to pick how they want to sign in;
/// <c>/login/challenge</c> then forwards them through the chosen provider and back to the original
/// authorize request.
/// </summary>
public static class LoginPageEndpoints
{
    public static void MapLoginEndpoints(this WebApplication app)
    {
        app.MapGet("/login", (string? returnUrl) =>
            Results.Content(RenderLoginPage(LocalReturnUrl(returnUrl)), "text/html; charset=utf-8"));

        app.MapGet("/login/challenge", (string provider, string? returnUrl) =>
        {
            var target = AuthProviders.ByHint(provider)
                ?? AuthProviders.All.FirstOrDefault(p =>
                    string.Equals(p.Scheme, provider, StringComparison.OrdinalIgnoreCase));

            if (target is null)
                return Results.BadRequest($"Unknown identity provider '{provider}'.");

            return Results.Challenge(
                new AuthenticationProperties { RedirectUri = LocalReturnUrl(returnUrl) },
                [target.Scheme]);
        });
    }

    /// <summary>
    /// Only permit local return URLs, so the chooser can never be used as an open redirect. Falls
    /// back to the app root.
    /// </summary>
    internal static string LocalReturnUrl(string? returnUrl) =>
        !string.IsNullOrEmpty(returnUrl)
        && Uri.IsWellFormedUriString(returnUrl, UriKind.Relative)
        && returnUrl.StartsWith('/')
        && !returnUrl.StartsWith("//")
            ? returnUrl
            : "/";

    private static string RenderLoginPage(string returnUrl)
    {
        var encodedReturn = UrlEncoder.Default.Encode(returnUrl);

        var buttons = new StringBuilder();
        foreach (var provider in AuthProviders.All)
        {
            var href = $"/login/challenge?provider={UrlEncoder.Default.Encode(provider.Hint)}&returnUrl={encodedReturn}";
            buttons.Append(
                $"<a class=\"provider\" href=\"{href}\">Continue with {HtmlEncoder.Default.Encode(provider.DisplayName)}</a>");
        }

        return $$"""
            <!doctype html>
            <html lang="en">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width, initial-scale=1">
              <title>Sign in to protostar</title>
              <style>
                :root { color-scheme: light dark; }
                body { font-family: system-ui, sans-serif; display: grid; place-items: center;
                       min-height: 100vh; margin: 0; background: #0d1117; color: #e6edf3; }
                main { width: min(22rem, 90vw); padding: 2rem; text-align: center; }
                h1 { margin: 0 0 .25rem; font-size: 1.5rem; }
                p { margin: 0 0 1.5rem; color: #9da7b3; }
                .provider { display: block; padding: .75rem 1rem; margin: .5rem 0; border-radius: .5rem;
                            background: #21262d; color: #e6edf3; text-decoration: none;
                            border: 1px solid #30363d; }
                .provider:hover { background: #30363d; }
              </style>
            </head>
            <body>
              <main>
                <h1>protostar</h1>
                <p>Choose how to sign in.</p>
                {{buttons}}
              </main>
            </body>
            </html>
            """;
    }
}
