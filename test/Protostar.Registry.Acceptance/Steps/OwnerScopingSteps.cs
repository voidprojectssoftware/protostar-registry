using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Protostar.Registry.Acceptance.Support;
using Reqnroll;
using Xunit;

namespace Protostar.Registry.Acceptance.Steps;

/// <summary>Steps for OwnerScoping.feature: alice backs things up, and bob tries every way to see them.</summary>
[Binding]
public sealed class OwnerScopingSteps
{
    private readonly RegistryScenario _scenario;
    private readonly string _folder = "/home/alice/src/demo";
    private readonly string _rootCommit = RegistryUser.NewCommit();
    private string? _sessionKey;
    private Guid? _projectId;
    private JsonNode? _resolved;

    public OwnerScopingSteps(RegistryScenario scenario) => _scenario = scenario;

    private RegistryUser Alice => _scenario.User("alice");

    private RegistryUser Bob => _scenario.User("bob");

    private string SessionKey => _sessionKey ?? throw new InvalidOperationException("alice has not backed up a session.");

    private Guid ProjectId => _projectId ?? throw new InvalidOperationException("alice has not backed up memory.");

    [Given("alice has backed up a skill named {string}")]
    public async Task GivenAliceHasBackedUpASkill(string name) =>
        Assert.Equal(HttpStatusCode.Created, (await Alice.PushSkillAsync(name)).StatusCode);

    [Given("alice has backed up a session mentioning {string}")]
    public async Task GivenAliceHasBackedUpASession(string phrase) =>
        _sessionKey = await Alice.BackUpSessionAsync($"Please summarise the {phrase} notes.");

    [Given("alice has backed up project memory mentioning {string}")]
    public async Task GivenAliceHasBackedUpProjectMemory(string phrase) =>
        _projectId = await Alice.PushMemoryAsync(RegistryUser.GitFolder(_folder, _rootCommit), "MEMORY.md", $"# Notes\n\nThe {phrase} survey runs in March.\n");

    [Then("alice's skill list contains {string}")]
    public async Task ThenAlicesSkillListContains(string name) =>
        Assert.Contains(name, SkillNames(await Alice.GetJsonAsync("/v1/skills?includeProjects=true")));

    [Then("bob's skill list is empty")]
    public async Task ThenBobsSkillListIsEmpty()
    {
        var list = await Bob.GetJsonAsync("/v1/skills?includeProjects=true");
        Assert.Empty(list["skills"]!.AsArray());
        Assert.Empty(list["removed"]!.AsArray());
    }

    [Then("bob downloading the skill {string} is refused with {int}")]
    public Task ThenBobDownloadingTheSkillIsRefused(string name, int status) =>
        AssertStatusAsync(Bob.Http.GetAsync($"/v1/skills/{name}/files"), status);

    [Then("alice's session list contains her session")]
    public async Task ThenAlicesSessionListContainsHerSession() =>
        Assert.Contains(SessionKey, Keys(await Alice.GetJsonAsync("/v1/transcripts")));

    [Then("bob's session list is empty")]
    public async Task ThenBobsSessionListIsEmpty() =>
        Assert.Empty((await Bob.GetJsonAsync("/v1/transcripts")).AsArray());

    [Then("bob reading alice's session is refused with {int}")]
    public Task ThenBobReadingAlicesSessionIsRefused(int status) =>
        AssertStatusAsync(Bob.Http.GetAsync($"/v1/transcripts/{RegistryUser.Harness}/{SessionKey}"), status);

    [Then("bob downloading alice's session is refused with {int}")]
    public Task ThenBobDownloadingAlicesSessionIsRefused(int status) =>
        AssertStatusAsync(Bob.Http.GetAsync($"/v1/transcripts/{RegistryUser.Harness}/{SessionKey}/content"), status);

    [Then("bob reading alice's session redacted is refused with {int}")]
    public Task ThenBobReadingAlicesSessionRedactedIsRefused(int status) =>
        AssertStatusAsync(Bob.Http.GetAsync($"/v1/transcripts/{RegistryUser.Harness}/{SessionKey}/content?redacted=true"), status);

    [Then("bob looking up alice's session finds it unknown")]
    public async Task ThenBobLookingUpAlicesSessionFindsItUnknown()
    {
        var response = await Bob.Http.PostAsJsonAsync("/v1/transcripts/lookup", new
        {
            transcripts = new[] { new { harness = RegistryUser.Harness, key = SessionKey } },
        });
        response.EnsureSuccessStatusCode();
        var entry = (await response.Content.ReadFromJsonAsync<JsonArray>())!.Single()!;
        Assert.False(entry["known"]!.GetValue<bool>());
        Assert.Null(entry["length"]);
    }

    [Then("alice's memory list contains her project")]
    public async Task ThenAlicesMemoryListContainsHerProject() =>
        Assert.Contains(ProjectId, (await Alice.GetJsonAsync("/v1/memory")).AsArray().Select(p => p!["projectId"]!.GetValue<Guid>()));

    [Then("bob's memory list is empty")]
    public async Task ThenBobsMemoryListIsEmpty() =>
        Assert.Empty((await Bob.GetJsonAsync("/v1/memory")).AsArray());

    [Then("bob listing the files of alice's project is refused with {int}")]
    public Task ThenBobListingTheFilesOfAlicesProjectIsRefused(int status) =>
        AssertStatusAsync(Bob.Http.GetAsync($"/v1/memory/{ProjectId}"), status);

    [Then("bob downloading the memory of alice's project is refused with {int}")]
    public Task ThenBobDownloadingTheMemoryOfAlicesProjectIsRefused(int status) =>
        AssertStatusAsync(Bob.Http.GetAsync($"/v1/memory/{ProjectId}/content"), status);

    [When("bob resolves the folder alice's memory came from")]
    public async Task WhenBobResolvesTheFolderAlicesMemoryCameFrom()
    {
        // The same repository (same root commit and remote) checked out on bob's machine: the case where matching
        // projects by repository identity must not cross owners.
        var response = await Bob.Http.PostAsJsonAsync("/v1/projects/resolve", new
        {
            machineId = await Bob.EnsureMachineAsync(),
            facts = RegistryUser.GitFolder(_folder, _rootCommit),
        });
        response.EnsureSuccessStatusCode();
        _resolved = await response.Content.ReadFromJsonAsync<JsonNode>();
    }

    [Then("bob is told the folder has no project yet")]
    public void ThenBobIsToldTheFolderHasNoProjectYet()
    {
        Assert.Null(_resolved!["projectId"]);
        Assert.Equal("new", _resolved["recognisedBy"]!.GetValue<string>());
        Assert.Empty(_resolved["suggestions"]!.AsArray());
    }

    [Then("bob linking a folder to alice's project is refused with {int}")]
    public async Task ThenBobLinkingAFolderToAlicesProjectIsRefused(int status) =>
        await AssertStatusAsync(Bob.Http.PostAsJsonAsync("/v1/projects/link", new
        {
            machineId = await Bob.EnsureMachineAsync(),
            facts = new { workingDirectory = "/home/bob/notes", directory = "/home/bob/notes", isGit = false },
            project = ProjectId.ToString(),
        }), status);

    [Then("alice's search for {string} finds her session and her memory")]
    public async Task ThenAlicesSearchFindsHerSessionAndHerMemory(string phrase)
    {
        var hits = (await Alice.GetJsonAsync(SearchPath(phrase))).AsArray();
        Assert.Contains(hits, h => h!["kind"]!.GetValue<string>() == "session" && h["key"]!.GetValue<string>() == SessionKey);
        Assert.Contains(hits, h => h!["kind"]!.GetValue<string>() == "memory" && h["projectId"]!.GetValue<Guid>() == ProjectId);
    }

    [Then("bob's search for {string} finds nothing")]
    public async Task ThenBobsSearchFindsNothing(string phrase) =>
        Assert.Empty((await Bob.GetJsonAsync(SearchPath(phrase))).AsArray());

    private static string SearchPath(string phrase) => $"/v1/search?q={Uri.EscapeDataString(phrase)}";

    private static IEnumerable<string> SkillNames(JsonNode list) =>
        list["skills"]!.AsArray().Select(s => s!["name"]!.GetValue<string>());

    private static IEnumerable<string> Keys(JsonNode transcripts) =>
        transcripts.AsArray().Select(t => t!["key"]!.GetValue<string>());

    private static async Task AssertStatusAsync(Task<HttpResponseMessage> request, int status)
    {
        var response = await request;
        Assert.True((int)response.StatusCode == status,
            $"Expected {status}, got {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    }
}
