using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Protostar.Registry.Api.Identity;

namespace Protostar.Registry.Tests.Identity;

/// <summary>Contract tests for <see cref="SignInRegistration"/>: the dev sign-in exists in Development and nowhere else.</summary>
/// <remarks>The dev sign-in signs anyone in as the local user, so offering it outside Development would be an open door.</remarks>
public sealed class SignInRegistrationTests
{
    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("Testing")]
    public async Task Outside_Development_the_dev_sign_in_is_neither_offered_nor_registered(string environmentName)
    {
        using var services = Compose(environmentName);

        var providers = services.GetRequiredService<AuthProviders>();
        Assert.False(providers.OffersDevelopmentSignIn);
        Assert.Null(providers.ByHint("dev"));
        Assert.DoesNotContain(providers.All, p => p.Scheme == DevelopmentSignInHandler.SchemeName);

        var schemes = await services.GetRequiredService<IAuthenticationSchemeProvider>().GetAllSchemesAsync();
        Assert.DoesNotContain(schemes, s => s.Name == DevelopmentSignInHandler.SchemeName);
        Assert.Contains(schemes, s => s.Name == AuthEndpoints.GitHubScheme);
    }

    [Fact]
    public async Task In_Development_the_dev_sign_in_is_offered_and_registered()
    {
        using var services = Compose(Environments.Development);

        Assert.NotNull(services.GetRequiredService<AuthProviders>().ByHint("dev"));
        var schemes = await services.GetRequiredService<IAuthenticationSchemeProvider>().GetAllSchemesAsync();
        Assert.Contains(schemes, s => s.Name == DevelopmentSignInHandler.SchemeName);
    }

    private static ServiceProvider Compose(string environmentName)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRegistrySignIn(new ConfigurationBuilder().Build(), new HostingEnvironment { EnvironmentName = environmentName });
        return services.BuildServiceProvider();
    }
}
