using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Protostar.Registry.Api.Identity;

/// <summary>
/// Seeds the browser-based <c>scalar-ui</c> OAuth client the Scalar API reference uses to run the
/// authorization-code + PKCE login and obtain a token for trying protected endpoints. A public client with
/// the Scalar page as its redirect URI. Registered only in Development; it must never exist in production.
/// </summary>
public sealed class ScalarUiClientSeeder(IServiceProvider services) : IHostedService
{
    public const string ClientId = "scalar-ui";

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = services.CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();

        if (await manager.FindByClientIdAsync(ClientId, cancellationToken) is not null)
            return;

        await manager.CreateAsync(new OpenIddictApplicationDescriptor
        {
            ClientId = ClientId,
            ClientType = ClientTypes.Public,
            ApplicationType = ApplicationTypes.Web,
            ConsentType = ConsentTypes.Implicit,
            DisplayName = "Scalar API reference (dev)",
            RedirectUris =
            {
                // Scalar's default callback is the reference page; register the base too, since some
                // Scalar versions send the origin instead of the full page path.
                new Uri("https://localhost:7443/scalar/v1"),
                new Uri("https://localhost:7443"),
            },
            Permissions =
            {
                Permissions.Endpoints.Authorization,
                Permissions.Endpoints.Token,
                Permissions.GrantTypes.AuthorizationCode,
                Permissions.ResponseTypes.Code,
                Permissions.Scopes.Email,
                Permissions.Scopes.Profile,
                Permissions.Prefixes.Scope + "registry",
            },
            Requirements =
            {
                Requirements.Features.ProofKeyForCodeExchange,
            },
        }, cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
