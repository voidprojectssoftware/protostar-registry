using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OAuth;

namespace Protostar.Registry.Api.Identity;

/// <summary>Registers the interactive sign-in: the cookie, GitHub, and in Development only the local dev user.</summary>
/// <remarks>
/// Kept apart from Program.cs so a test can compose it for a given environment and prove that a
/// non-Development host never offers the dev sign-in, which would let anyone sign in as the local user.
/// </remarks>
public static class SignInRegistration
{
    /// <summary>Adds authentication schemes and the <see cref="AuthProviders"/> the sign-in page offers.</summary>
    /// <remarks>
    /// GitHub falls back to placeholder credentials so the app boots without them (tests, plain
    /// <c>dotnet run</c>); only real <c>GitHub:ClientId</c> and <c>GitHub:ClientSecret</c> make the GitHub leg
    /// work. GitHub calls back to <c>/signin-github</c> (the handler's default <c>CallbackPath</c>) on the
    /// request's scheme and host, so behind a proxy that is the public origin once forwarded headers apply.
    /// </remarks>
    public static IServiceCollection AddRegistrySignIn(
        this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var providers = AuthProviders.For(environment);
        services.AddSingleton(providers);

        var authentication = services.AddAuthentication(options =>
        {
            options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
            options.DefaultSignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
            options.DefaultChallengeScheme = AuthEndpoints.GitHubScheme;
        })
        .AddCookie()
        .AddGitHub(AuthEndpoints.GitHubScheme, options =>
        {
            options.ClientId = configuration["GitHub:ClientId"] ?? "placeholder";
            options.ClientSecret = configuration["GitHub:ClientSecret"] ?? "placeholder";
            options.Scope.Add("read:user");
            options.Scope.Add("user:email");
            options.SaveTokens = true;

            // GitHub's default claim mapping covers id + login; capture display name and avatar too.
            options.Events.OnCreatingTicket = context =>
            {
                AddJsonClaim(context, "urn:github:name", "name");
                AddJsonClaim(context, "urn:github:avatar", "avatar_url");
                return Task.CompletedTask;
            };
        });

        if (providers.OffersDevelopmentSignIn)
            authentication.AddScheme<AuthenticationSchemeOptions, DevelopmentSignInHandler>(DevelopmentSignInHandler.SchemeName, null);

        return services;
    }

    private static void AddJsonClaim(OAuthCreatingTicketContext context, string claimType, string jsonKey)
    {
        if (context.User.TryGetProperty(jsonKey, out var value) &&
            value.ValueKind == JsonValueKind.String &&
            value.GetString() is { Length: > 0 } text)
        {
            context.Identity?.AddClaim(new Claim(claimType, text));
        }
    }
}
