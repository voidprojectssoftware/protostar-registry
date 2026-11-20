using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Protostar.Registry.Api.Infrastructure;

namespace Protostar.Registry.Tests.Infrastructure;

/// <summary>Contract tests for <see cref="ForwardedHeadersRegistration"/>.</summary>
public sealed class ForwardedHeadersRegistrationTests
{
    [Fact]
    public void Configured_proxies_and_networks_are_trusted_alongside_loopback()
    {
        var options = Configure(("ForwardedHeaders:KnownProxies", "192.0.2.10, 2001:db8::1"),
            ("ForwardedHeaders:KnownNetworks", "172.16.0.0/12;10.0.0.0/8"));

        Assert.Contains(IPAddress.Parse("192.0.2.10"), options.KnownProxies);
        Assert.Contains(IPAddress.Parse("2001:db8::1"), options.KnownProxies);
        Assert.Contains(IPAddress.IPv6Loopback, options.KnownProxies);
        Assert.Contains(System.Net.IPNetwork.Parse("127.0.0.0/8"), options.KnownIPNetworks);
        Assert.Contains(System.Net.IPNetwork.Parse("172.16.0.0/12"), options.KnownIPNetworks);
        Assert.Contains(System.Net.IPNetwork.Parse("10.0.0.0/8"), options.KnownIPNetworks);
    }

    [Fact]
    public void Scheme_host_and_client_address_are_forwarded()
    {
        var options = Configure();

        Assert.Equal(
            Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor
                | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto
                | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedHost,
            options.ForwardedHeaders);
    }

    [Theory]
    [InlineData("ForwardedHeaders:KnownProxies", "proxy-vm")]
    [InlineData("ForwardedHeaders:KnownNetworks", "172.16.0.0")]
    public void An_entry_that_does_not_parse_stops_startup(string key, string value)
    {
        var error = Assert.Throws<InvalidOperationException>(() => Configure((key, value)));

        Assert.Contains(value, error.Message);
    }

    private static ForwardedHeadersOptions Configure(params (string Key, string Value)[] settings)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();
        using var services = new ServiceCollection().AddRegistryForwardedHeaders(configuration).BuildServiceProvider();
        return services.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value;
    }
}
