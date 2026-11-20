using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Protostar.Registry.Api.Identity;

/// <summary>
/// Seeds the browser-based <c>scalar-ui</c> OAuth client the Scalar API reference uses to run the
/// authorization-code + PKCE login and obtain a token for trying protected endpoints.
/// </summary>
/// <remarks>
/// A public client whose redirect URIs are the Scalar page on each HTTPS address this host listens on
/// (the <c>urls</c> setting, which Aspire fills from the AppHost's port). A web client's redirect must match
/// exactly, port included, so a database that was seeded while the API ran on another port is updated
/// rather than left pointing there. Registered only in Development; it must never exist in production.
/// </remarks>
public sealed class ScalarUiClientSeeder : IHostedService
{
    public const string ClientId = "scalar-ui";

    private const string FallbackOrigin = "https://localhost:7443";

    private readonly IServiceProvider _services;
    private readonly IConfiguration _configuration;

    public ScalarUiClientSeeder(IServiceProvider services, IConfiguration configuration)
    {
        _services = services;
        _configuration = configuration;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = _services.CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();

        var descriptor = new OpenIddictApplicationDescriptor();
        var existing = await manager.FindByClientIdAsync(ClientId, cancellationToken);
        if (existing is not null)
            await manager.PopulateAsync(descriptor, existing, cancellationToken);

        var redirectUris = RedirectUris();
        if (existing is not null && descriptor.RedirectUris.SetEquals(redirectUris))
            return;

        descriptor.RedirectUris.Clear();
        descriptor.RedirectUris.UnionWith(redirectUris);

        if (existing is not null)
        {
            await manager.UpdateAsync(existing, descriptor, cancellationToken);
            return;
        }

        descriptor.ClientId = ClientId;
        descriptor.ClientType = ClientTypes.Public;
        descriptor.ApplicationType = ApplicationTypes.Web;
        descriptor.ConsentType = ConsentTypes.Implicit;
        descriptor.DisplayName = "Scalar API reference (dev)";
        descriptor.Permissions.UnionWith(
        [
            Permissions.Endpoints.Authorization,
            Permissions.Endpoints.Token,
            Permissions.GrantTypes.AuthorizationCode,
            Permissions.ResponseTypes.Code,
            Permissions.Scopes.Email,
            Permissions.Scopes.Profile,
            Permissions.Prefixes.Scope + "registry",
        ]);
        descriptor.Requirements.Add(Requirements.Features.ProofKeyForCodeExchange);
        await manager.CreateAsync(descriptor, cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    // Scalar's default callback is the reference page; register the origin too, since some Scalar
    // versions send the origin instead of the full page path.
    private HashSet<Uri> RedirectUris()
    {
        var origins = (_configuration["urls"] ?? string.Empty)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(url => url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            .DefaultIfEmpty(FallbackOrigin);

        return origins
            .Select(origin => new Uri(origin.TrimEnd('/')))
            .SelectMany(origin => new[] { new Uri(origin, "/scalar/v1"), origin })
            .ToHashSet();
    }
}
