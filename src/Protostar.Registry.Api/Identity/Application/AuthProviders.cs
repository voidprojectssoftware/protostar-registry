namespace Protostar.Registry.Api.Identity;

/// <summary>An external identity provider the registry offers on its sign-in page.</summary>
/// <param name="Scheme">The ASP.NET Core authentication scheme name.</param>
/// <param name="DisplayName">Label shown on the sign-in button.</param>
/// <param name="Hint">Short token a client passes (e.g. <c>identity_provider=github</c>) to skip the chooser.</param>
public sealed record AuthProvider(string Scheme, string DisplayName, string Hint);

/// <summary>The identity providers this host offers, registered once in DI.</summary>
/// <remarks>
/// To add a provider, register its authentication handler in <see cref="SignInRegistration"/> and add an
/// entry here; the sign-in page and the provider-hint shortcut pick it up automatically. The list depends on
/// the host environment only, and <see cref="SignInRegistration"/> registers the Development sign-in scheme
/// from <see cref="OffersDevelopmentSignIn"/>, so the page and the schemes cannot disagree.
/// </remarks>
public sealed class AuthProviders
{
    private static readonly AuthProvider GitHub = new(AuthEndpoints.GitHubScheme, "GitHub", "github");

    private static readonly AuthProvider DevelopmentLocal =
        new(DevelopmentSignInHandler.SchemeName, "Local dev user (Development only)", "dev");

    private AuthProviders(IReadOnlyList<AuthProvider> all) => All = all;

    /// <summary>The providers for <paramref name="environment"/>: GitHub everywhere, plus the dev sign-in in Development only.</summary>
    public static AuthProviders For(IHostEnvironment environment) =>
        new(environment.IsDevelopment() ? [GitHub, DevelopmentLocal] : [GitHub]);

    /// <summary>The providers offered on the sign-in page, in display order.</summary>
    public IReadOnlyList<AuthProvider> All { get; }

    /// <summary>Whether the <see cref="DevelopmentSignInHandler"/> provider (hint <c>dev</c>) is offered.</summary>
    public bool OffersDevelopmentSignIn => All.Contains(DevelopmentLocal);

    /// <summary>Resolves a provider by its <see cref="AuthProvider.Hint"/> (case-insensitive).</summary>
    public AuthProvider? ByHint(string? hint) =>
        string.IsNullOrWhiteSpace(hint)
            ? null
            : All.FirstOrDefault(p => string.Equals(p.Hint, hint, StringComparison.OrdinalIgnoreCase));
}
