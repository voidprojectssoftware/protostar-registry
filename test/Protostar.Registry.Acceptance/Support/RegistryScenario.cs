using System.Buffers.Text;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Npgsql;
using Xunit;

namespace Protostar.Registry.Acceptance.Support;

/// <summary>One scenario's registry: its own empty database and bucket, the in-process API over them, and its signed-in users.</summary>
/// <remarks>
/// <para>
/// A fresh database per scenario, not per feature: scenarios stay independent whatever order they run in, the
/// migrations scenario needs a database nothing has touched, and creating a database on the shared server takes
/// milliseconds. The host is built per scenario too, because a host is bound to one database. Reqnroll creates one
/// of these per scenario and <see cref="ScenarioHooks"/> disposes it, dropping the database.
/// </para>
/// <para>
/// Users sign in the way the CLI does: the browser leg (here <see cref="AcceptanceSignIn"/> in place of GitHub), then
/// <c>/connect/authorize</c> with PKCE for the public <c>protostar-cli</c> client the registry seeds, then the code
/// exchanged at <c>/connect/token</c>. The access token is the registry's own, validated by its own middleware.
/// </para>
/// </remarks>
public sealed class RegistryScenario
{
    private static readonly Uri BaseAddress = new("https://localhost");

    private readonly Dictionary<string, RegistryUser> _users = new(StringComparer.Ordinal);
    private readonly string _bucket = "acceptance-" + Guid.NewGuid().ToString("N")[..20];
    private RegistryFactory? _factory;
    private bool _databaseCreated;

    /// <summary>This scenario's database on the shared server.</summary>
    public string Database { get; } = "acceptance_" + Guid.NewGuid().ToString("N");

    /// <summary>The running registry. Throws until <see cref="StartAsync"/> has run.</summary>
    public RegistryFactory Factory =>
        _factory ?? throw new InvalidOperationException("The registry has not been started in this scenario.");

    /// <summary>Creates this scenario's database, empty.</summary>
    public async Task CreateDatabaseAsync()
    {
        await using var connection = new NpgsqlConnection(RegistryInfrastructure.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"CREATE DATABASE \"{Database}\"", connection);
        await command.ExecuteNonQueryAsync();
        _databaseCreated = true;
    }

    /// <summary>How many tables this scenario's database has in its <c>public</c> schema.</summary>
    public async Task<long> CountTablesAsync()
    {
        await using var connection = new NpgsqlConnection(RegistryInfrastructure.ConnectionString(Database));
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public'", connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>Starts the registry over this scenario's database, creating the database first if needed.</summary>
    /// <remarks>Returns once Program has migrated the database and the hosted services (the client seeder) have started.</remarks>
    public async Task StartAsync()
    {
        if (!_databaseCreated)
            await CreateDatabaseAsync();
        _factory = new RegistryFactory(Database, _bucket);
        _ = _factory.Server;
    }

    /// <summary>An HTTP client with no credentials.</summary>
    public HttpClient CreateAnonymousClient() => Factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = BaseAddress });

    /// <summary>Signs <paramref name="login"/> in through the OAuth flow the CLI uses and returns them with their access token.</summary>
    public async Task<RegistryUser> SignInAsync(string login)
    {
        using var browser = Factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = BaseAddress,
            AllowAutoRedirect = false,
            HandleCookies = true,
        });

        (await browser.GetAsync($"{AcceptanceSignIn.Path}?login={Uri.EscapeDataString(login)}")).EnsureSuccessStatusCode();

        const string redirectUri = "http://127.0.0.1/callback";
        var verifier = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var authorize = QueryHelpers.AddQueryString("/connect/authorize", new Dictionary<string, string?>
        {
            ["response_type"] = "code",
            ["client_id"] = "protostar-cli",
            ["redirect_uri"] = redirectUri,
            ["scope"] = "openid profile registry",
            ["state"] = "acceptance",
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
        });

        var redirect = await browser.GetAsync(authorize);
        Assert.Equal(HttpStatusCode.Redirect, redirect.StatusCode);
        var callback = redirect.Headers.Location!;
        Assert.StartsWith(redirectUri, callback.ToString());
        var code = QueryHelpers.ParseQuery(callback.Query)["code"].ToString();

        var exchange = await browser.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = "protostar-cli",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["code_verifier"] = verifier,
        }));
        exchange.EnsureSuccessStatusCode();
        var accessToken = (await exchange.Content.ReadFromJsonAsync<JsonObject>())!["access_token"]!.GetValue<string>();

        var http = CreateAnonymousClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var user = new RegistryUser(login, http);
        _users[login] = user;
        return user;
    }

    /// <summary>A user signed in earlier in this scenario.</summary>
    public RegistryUser User(string login) =>
        _users.TryGetValue(login, out var user) ? user : throw new InvalidOperationException($"{login} has not signed in in this scenario.");

    /// <summary>Stops the registry and drops this scenario's database.</summary>
    public async Task DisposeAsync()
    {
        foreach (var user in _users.Values)
            user.Http.Dispose();
        if (_factory is not null)
            await _factory.DisposeAsync();
        if (!_databaseCreated)
            return;

        await using var connection = new NpgsqlConnection(RegistryInfrastructure.AdminConnectionString);
        await connection.OpenAsync();
        // FORCE ends the host's pooled connections, which outlive the host.
        await using var command = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{Database}\" WITH (FORCE)", connection);
        await command.ExecuteNonQueryAsync();
    }
}
