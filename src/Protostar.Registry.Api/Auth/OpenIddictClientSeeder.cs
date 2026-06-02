using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Protostar.Registry.Api.Auth;

/// <summary>
/// Seeds the public <c>protostar-cli</c> OAuth client on startup if it is absent. The CLI is a
/// native, public client (no secret) that authenticates with Authorization Code + PKCE over a
/// loopback redirect (RFC 8252). OpenIddict special-cases loopback redirect URIs and ignores the
/// port at validation time, so the CLI may listen on any ephemeral 127.0.0.1 port.
/// </summary>
public sealed class OpenIddictClientSeeder(IServiceProvider services) : IHostedService
{
    public const string CliClientId = "protostar-cli";

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = services.CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();

        if (await manager.FindByClientIdAsync(CliClientId, cancellationToken) is not null)
            return;

        await manager.CreateAsync(new OpenIddictApplicationDescriptor
        {
            ClientId = CliClientId,
            ClientType = ClientTypes.Public,
            ApplicationType = ApplicationTypes.Native,
            ConsentType = ConsentTypes.Implicit,
            DisplayName = "protostar CLI",
            RedirectUris =
            {
                new Uri("http://127.0.0.1/callback"),
                new Uri("http://localhost/callback"),
            },
            Permissions =
            {
                Permissions.Endpoints.Authorization,
                Permissions.Endpoints.Token,
                Permissions.Endpoints.EndSession,
                Permissions.GrantTypes.AuthorizationCode,
                Permissions.GrantTypes.RefreshToken,
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
