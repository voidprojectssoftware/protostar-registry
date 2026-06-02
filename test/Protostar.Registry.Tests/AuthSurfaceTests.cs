using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Protostar.Registry.Tests;

public sealed class AuthSurfaceTests(RegistryApiFactory factory) : IClassFixture<RegistryApiFactory>
{
    // OpenIddict only serves its endpoints over HTTPS. The in-memory TestServer infers the request
    // scheme from the client base address, so an https base address satisfies that without real TLS.
    private HttpClient CreateClient() =>
        factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost"),
        });

    [Fact]
    public async Task Meta_advertises_service_and_api_majors()
    {
        var client = CreateClient();

        var response = await client.GetAsync("/v1/meta");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var meta = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("protostar-registry", meta.GetProperty("service").GetString());

        var majors = meta.GetProperty("apiMajors").EnumerateArray().Select(e => e.GetInt32()).ToArray();
        Assert.Contains(1, majors);
    }

    [Fact]
    public async Task UserInfo_without_a_token_is_unauthorized()
    {
        var client = CreateClient();

        var response = await client.GetAsync("/connect/userinfo");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Discovery_document_exposes_the_oauth_endpoints()
    {
        var client = CreateClient();

        var response = await client.GetAsync("/.well-known/openid-configuration");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var doc = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(doc.TryGetProperty("authorization_endpoint", out _));
        Assert.True(doc.TryGetProperty("token_endpoint", out _));
        Assert.True(doc.TryGetProperty("userinfo_endpoint", out _));
    }
}
