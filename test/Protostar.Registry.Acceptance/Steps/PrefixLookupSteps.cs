using System.Net;
using Protostar.Registry.Acceptance.Support;
using Reqnroll;
using Xunit;

namespace Protostar.Registry.Acceptance.Steps;

/// <summary>Steps for FindingSessionsByPrefix.feature: sessions backed up under chosen keys, then looked up by prefix.</summary>
[Binding]
public sealed class PrefixLookupSteps
{
    private readonly RegistryScenario _scenario;
    private HttpResponseMessage? _response;

    public PrefixLookupSteps(RegistryScenario scenario) => _scenario = scenario;

    private HttpResponseMessage Response => _response ?? throw new InvalidOperationException("No prefix has been looked up.");

    [Given("{word} has backed up sessions with these keys")]
    public async Task GivenHasBackedUpSessionsWithTheseKeys(string login, DataTable table)
    {
        foreach (var row in table.Rows)
            await _scenario.User(login).BackUpSessionAsync("hello", row["key"]);
    }

    [Given("{word} has backed up a session with the key {string}")]
    public Task GivenHasBackedUpASessionWithTheKey(string login, string key) =>
        _scenario.User(login).BackUpSessionAsync("hello", key);

    [When("{word} looks up the key prefix {string}")]
    public async Task WhenLooksUpTheKeyPrefix(string login, string prefix) =>
        _response = await _scenario.User(login).Http.GetAsync($"/v1/transcripts?keyPrefix={Uri.EscapeDataString(prefix)}");

    [When("{word} looks up the key prefix {string} with a limit of {int}")]
    public async Task WhenLooksUpTheKeyPrefixWithALimit(string login, string prefix, int limit) =>
        _response = await _scenario.User(login).Http.GetAsync($"/v1/transcripts?keyPrefix={Uri.EscapeDataString(prefix)}&limit={limit}");

    [Then("{word} finds exactly these sessions")]
    public async Task ThenFindsExactlyTheseSessions(string pronoun, DataTable table) =>
        Assert.Equal(table.Rows.Select(row => row["key"]).Order(StringComparer.Ordinal), (await FoundKeysAsync()).Order(StringComparer.Ordinal));

    [Then("{word} finds {int} sessions")]
    public async Task ThenFindsSessions(string pronoun, int count) =>
        Assert.Equal(count, (await FoundKeysAsync()).Count);

    [Then("{word} finds no sessions")]
    public async Task ThenFindsNoSessions(string pronoun) =>
        Assert.Empty(await FoundKeysAsync());

    [Then("the lookup is refused with {int}")]
    public async Task ThenTheLookupIsRefusedWith(int status)
    {
        var body = await Response.Content.ReadAsStringAsync();
        Assert.True((int)Response.StatusCode == status, $"Expected {status}, got {(int)Response.StatusCode}: {body}");
        Assert.Contains("keyPrefix", body);
    }

    private async Task<IReadOnlyList<string>> FoundKeysAsync()
    {
        var body = await Response.Content.ReadAsStringAsync();
        Assert.True(Response.StatusCode == HttpStatusCode.OK, $"The lookup answered {(int)Response.StatusCode}: {body}");
        return System.Text.Json.Nodes.JsonNode.Parse(body)!.AsArray().Select(t => t!["key"]!.GetValue<string>()).ToList();
    }
}
