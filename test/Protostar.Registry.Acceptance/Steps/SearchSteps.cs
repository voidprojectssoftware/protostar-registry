using System.Text.Json.Nodes;
using Protostar.Registry.Acceptance.Support;
using Reqnroll;
using Xunit;

namespace Protostar.Registry.Acceptance.Steps;

/// <summary>Steps for Search.feature: a phrase is found, the secret beside it is redacted out of the index.</summary>
[Binding]
public sealed class SearchSteps
{
    // A well-formed GitHub token (the prefix, then 36 characters), assembled here so the literal is not in the repository.
    private static readonly string GitHubToken = "gh" + "p_" + "abcdefghijklmnopqrstuvwxyz0123456789";

    private readonly RegistryScenario _scenario;
    private string? _sessionKey;
    private Guid? _projectId;
    private JsonArray? _hits;

    public SearchSteps(RegistryScenario scenario) => _scenario = scenario;

    private JsonArray Hits => _hits ?? throw new InvalidOperationException("No search has been made in this scenario.");

    [Given("{word} has backed up a session saying {string}")]
    public async Task GivenHasBackedUpASessionSaying(string login, string text) =>
        _sessionKey = await _scenario.User(login).BackUpSessionAsync(Expand(text));

    [Given("{word} has backed up a memory saying {string}")]
    public async Task GivenHasBackedUpAMemorySaying(string login, string text) =>
        _projectId = await _scenario.User(login).PushMemoryAsync(
            RegistryUser.GitFolder($"/home/{login}/src/demo", RegistryUser.NewCommit()), "MEMORY.md", Expand(text) + "\n");

    [When("{word} searches for {string}")]
    public async Task WhenSearchesFor(string login, string query) =>
        _hits = (await _scenario.User(login).GetJsonAsync($"/v1/search?q={Uri.EscapeDataString(Expand(query))}")).AsArray();

    [Then("the search finds her session")]
    public void ThenTheSearchFindsHerSession()
    {
        var hit = Assert.Single(Hits)!;
        Assert.Equal("session", hit["kind"]!.GetValue<string>());
        Assert.Equal(_sessionKey, hit["key"]!.GetValue<string>());
    }

    [Then("the search finds her memory")]
    public void ThenTheSearchFindsHerMemory()
    {
        var hit = Assert.Single(Hits)!;
        Assert.Equal("memory", hit["kind"]!.GetValue<string>());
        Assert.Equal(_projectId, hit["projectId"]!.GetValue<Guid>());
        Assert.Equal("MEMORY.md", hit["path"]!.GetValue<string>());
    }

    [Then("the snippet marks {string} and {string} as matches")]
    public void ThenTheSnippetMarksAsMatches(string first, string second)
    {
        Assert.Contains($"«{first}»", Snippet);
        Assert.Contains($"«{second}»", Snippet);
    }

    [Then("the snippet shows {string}")]
    public void ThenTheSnippetShows(string text) => Assert.Contains(Expand(text), Snippet);

    [Then("the snippet does not contain {string}")]
    public void ThenTheSnippetDoesNotContain(string text) => Assert.DoesNotContain(Expand(text), Snippet);

    [Then("the search finds nothing")]
    public void ThenTheSearchFindsNothing() => Assert.Empty(Hits);

    private string Snippet => Assert.Single(Hits)!["snippet"]!.GetValue<string>();

    private static string Expand(string text) => text.Replace("{github-token}", GitHubToken);
}
