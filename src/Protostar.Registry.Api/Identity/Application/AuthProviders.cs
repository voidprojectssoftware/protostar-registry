namespace Protostar.Registry.Api.Identity;

/// <summary>An external identity provider the registry offers on its sign-in page.</summary>
/// <param name="Scheme">The ASP.NET Core authentication scheme name.</param>
/// <param name="DisplayName">Label shown on the sign-in button.</param>
/// <param name="Hint">Short token a client passes (e.g. <c>identity_provider=github</c>) to skip the chooser.</param>
public sealed record AuthProvider(string Scheme, string DisplayName, string Hint);

/// <summary>
/// The identity providers wired into the registry. To add one, register its authentication handler
/// in Program.cs and add an entry here; the sign-in page and the provider-hint shortcut pick it up
/// automatically.
/// </summary>
public static class AuthProviders
{
    private static readonly AuthProvider GitHub = new(AuthEndpoints.GitHubScheme, "GitHub", "github");

    private static readonly AuthProvider DevelopmentLocal =
        new(DevelopmentSignInHandler.SchemeName, "Local dev user (Development only)", "dev");

    /// <summary>The providers offered on the sign-in page, in display order.</summary>
    public static IReadOnlyList<AuthProvider> All { get; private set; } = [GitHub];

    /// <summary>
    /// Adds the <see cref="DevelopmentSignInHandler"/> provider (hint <c>dev</c>). Call once at startup,
    /// and only in the Development environment, after registering its authentication scheme.
    /// </summary>
    public static void EnableDevelopmentSignIn() => All = [GitHub, DevelopmentLocal];

    /// <summary>Resolves a provider by its <see cref="AuthProvider.Hint"/> (case-insensitive).</summary>
    public static AuthProvider? ByHint(string? hint) =>
        string.IsNullOrWhiteSpace(hint)
            ? null
            : All.FirstOrDefault(p => string.Equals(p.Hint, hint, StringComparison.OrdinalIgnoreCase));
}
