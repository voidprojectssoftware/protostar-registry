using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace Protostar.Registry.Api.Infrastructure;

/// <summary>Configures which reverse proxies the registry believes about the original scheme, host and client address.</summary>
/// <remarks>
/// <para>
/// Behind a TLS-terminating proxy the API sees plain HTTP from the proxy's address. OpenIddict builds its issuer,
/// discovery document and redirects from the request, and refuses plain-HTTP requests, so the request must carry
/// the public <c>https://host</c> the proxy received. <c>UseForwardedHeaders</c> rewrites it from
/// <c>X-Forwarded-Proto</c>, <c>X-Forwarded-Host</c> and <c>X-Forwarded-For</c>, but only for requests whose
/// immediate peer is a known proxy; anyone else could otherwise claim any host.
/// </para>
/// <para>
/// Configuration: <c>ForwardedHeaders:KnownProxies</c> (IP addresses) and <c>ForwardedHeaders:KnownNetworks</c>
/// (CIDR ranges), each a comma-separated list, added to ASP.NET Core's loopback defaults. An entry that does not
/// parse stops startup rather than being ignored.
/// </para>
/// </remarks>
public static class ForwardedHeadersRegistration
{
    public const string Section = "ForwardedHeaders";

    /// <summary>Binds <see cref="ForwardedHeadersOptions"/> from the <c>ForwardedHeaders</c> section.</summary>
    /// <exception cref="InvalidOperationException">An entry is not an IP address or CIDR range.</exception>
    public static IServiceCollection AddRegistryForwardedHeaders(this IServiceCollection services, IConfiguration configuration)
    {
        var proxies = Entries(configuration, "KnownProxies")
            .Select(text => IPAddress.TryParse(text, out var address) ? address : throw Invalid("KnownProxies", text))
            .ToList();
        var networks = Entries(configuration, "KnownNetworks")
            .Select(text => System.Net.IPNetwork.TryParse(text, out var network) ? network : throw Invalid("KnownNetworks", text))
            .ToList();

        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
            foreach (var proxy in proxies)
                options.KnownProxies.Add(proxy);
            foreach (var network in networks)
                options.KnownIPNetworks.Add(network);
        });

        return services;
    }

    private static string[] Entries(IConfiguration configuration, string key) =>
        (configuration[$"{Section}:{key}"] ?? string.Empty)
            .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static InvalidOperationException Invalid(string key, string text) =>
        new($"{Section}__{key} has '{text}', which is not a valid {(key == "KnownProxies" ? "IP address" : "CIDR range")}.");
}
