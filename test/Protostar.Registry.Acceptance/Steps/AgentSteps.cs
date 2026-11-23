using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Protostar.Registry.Acceptance.Support;
using Reqnroll;
using Xunit;

namespace Protostar.Registry.Acceptance.Steps;

/// <summary>Steps for AgentBackup.feature: agent pushes, refusals, and project agents following a folder.</summary>
[Binding]
public sealed class AgentSteps
{
    private readonly RegistryScenario _scenario;
    private HttpResponseMessage? _push;
    private JsonObject? _body;
    private HttpResponseMessage? _link;
    private JsonObject? _linkBody;
    private Guid _repositoryProject;

    public AgentSteps(RegistryScenario scenario) => _scenario = scenario;

    private RegistryUser Alice => _scenario.User("alice");

    private JsonObject Body => _body ?? throw new InvalidOperationException("No agent push has been made in this scenario.");

    [Given("{word} has pushed the agent {string} twice, reaching version {int}")]
    public async Task GivenHasPushedTheAgentTwice(string login, string name, int version)
    {
        var user = _scenario.User(login);
        await AssertStoredAsync(await user.PushAgentAsync(name, baseVersion: 0), 1);
        await AssertStoredAsync(await user.PushAgentAsync(name, baseVersion: 1), version);
    }

    [Given("{word} has deleted the agent {string}")]
    public async Task GivenHasDeletedTheAgent(string login, string name) =>
        Assert.Equal(HttpStatusCode.NoContent, (await _scenario.User(login).Http.DeleteAsync($"/v1/agents/{name}")).StatusCode);

    [Given("{word} has renamed the agent {string} to {string}")]
    public async Task GivenHasRenamedTheAgent(string login, string name, string newName) =>
        Assert.Equal(HttpStatusCode.OK, (await _scenario.User(login).Http.PostAsJsonAsync($"/v1/agents/{name}/rename", new { newName })).StatusCode);

    [When("{word} pushes different content for the agent {string} based on version {int}")]
    public Task WhenPushesDifferentContent(string login, string name, int baseVersion) =>
        RecordAsync(_scenario.User(login).PushAgentAsync(name, baseVersion));

    [When("alice pushes an agent file with no name in its front matter")]
    public Task WhenAlicePushesAnAgentWithNoName() =>
        RecordAsync(Alice.PushAgentAsync("nameless", markdown: "---\ndescription: Has no name.\n---\nBody.\n"));

    [When("alice pushes the agent {string} from {string}")]
    public Task WhenAlicePushesTheAgentFrom(string name, string path) => RecordAsync(Alice.PushAgentAsync(name, path: path));

    [When("alice pushes the agent {string} in the project of a folder")]
    public Task WhenAlicePushesTheAgentInAProject(string name) =>
        RecordAsync(Alice.PushAgentAsync(name, baseVersion: 0, project: RegistryUser.GitFolder("/home/alice/src/demo", RegistryUser.NewCommit())));

    [Then("the agent push is refused with {int} and problem type {string}")]
    public void ThenTheAgentPushIsRefused(int status, string type)
    {
        Assert.Equal(status, (int)_push!.StatusCode);
        Assert.Equal(type, Body["type"]!.GetValue<string>());
    }

    [Then("the agent push is refused with {int} and a title mentioning {string}")]
    public void ThenTheAgentPushIsRefusedWithTitle(int status, string text)
    {
        Assert.Equal(status, (int)_push!.StatusCode);
        Assert.Contains(text, Body["title"]!.GetValue<string>());
    }

    [Then("the refusal says the current version is {int}")]
    public void ThenTheRefusalSaysTheCurrentVersionIs(int version) => Assert.Equal(version, Body["currentVersion"]!.GetValue<int>());

    [Then("the refusal says the last version was {int}")]
    public void ThenTheRefusalSaysTheLastVersionWas(int version) => Assert.Equal(version, Body["lastVersion"]!.GetValue<int>());

    [Then("the refusal says the agent was renamed to {string}")]
    public void ThenTheRefusalSaysTheAgentWasRenamedTo(string name) => Assert.Equal(name, Body["renamedTo"]!.GetValue<string>());

    [Then("the agent push is stored as version {int}")]
    public Task ThenTheAgentPushIsStoredAsVersion(int version) => AssertStoredAsync(_push!, version);

    [Then("pushing the same file again answers 200 at version {int}")]
    public async Task ThenPushingTheSameFileAgainIsUnchanged(int version)
    {
        var again = await Alice.PushAgentAsync("reviewer", baseVersion: 0, markdown: Alice.LastAgentFile);
        var body = await again.Content.ReadAsStringAsync();
        Assert.True(again.StatusCode == HttpStatusCode.OK, $"Expected 200, got {(int)again.StatusCode}: {body}");
        Assert.Equal(version, JsonNode.Parse(body)!["version"]!.GetValue<int>());
    }

    [Then("{word}'s agent {string} is still at version {int}")]
    public async Task ThenTheAgentIsStillAtVersion(string login, string name, int version)
    {
        var agent = (await _scenario.User(login).GetJsonAsync("/v1/agents"))["agents"]!.AsArray()
            .Single(a => a!["name"]!.GetValue<string>() == name)!;
        Assert.Equal(version, agent["version"]!.GetValue<int>());
    }

    [Then("{word} has no agent named {string}")]
    public async Task ThenHasNoAgentNamed(string login, string name)
    {
        var agents = (await _scenario.User(login).GetJsonAsync("/v1/agents?includeProjects=true"))["agents"]!.AsArray();
        Assert.DoesNotContain(agents, a => a!["name"]!.GetValue<string>() == name);
    }

    [Then("alice's removed agents include {string} at version {int}")]
    public async Task ThenAlicesRemovedAgentsInclude(string name, int version)
    {
        var removed = (await Alice.GetJsonAsync("/v1/agents"))["removed"]!.AsArray().Single(r => r!["name"]!.GetValue<string>() == name)!;
        Assert.Equal(version, removed["lastVersion"]!.GetValue<int>());
        Assert.Null(removed["renamedTo"]);
    }

    [Then("alice's agent list has {string} at path {string}")]
    public async Task ThenAlicesAgentListHasAtPath(string name, string path)
    {
        var agent = (await Alice.GetJsonAsync("/v1/agents"))["agents"]!.AsArray().Single(a => a!["name"]!.GetValue<string>() == name)!;
        Assert.Equal(path, agent["path"]!.GetValue<string>());
        Assert.Equal("An acceptance test agent.", agent["description"]!.GetValue<string>());
    }

    [Given("alice has the agent {string} in the project of the folder {string}")]
    public async Task GivenAliceHasTheAgentInTheProjectOfTheFolder(string name, string folder) =>
        await AssertStoredAsync(await Alice.PushAgentAsync(name, baseVersion: 0, project: RegistryUser.PlainFolder(folder)), 1);

    [Given("alice has a repository project that already has a skill named {string}")]
    public async Task GivenARepositoryProjectWithASkill(string name)
    {
        var pushed = await Alice.PushSkillAsync(name, baseVersion: 0, project: RegistryUser.GitFolder("/home/alice/src/widget", RegistryUser.NewCommit()));
        _repositoryProject = await ProjectOfAsync(pushed);
    }

    [Given("alice has a repository project that already has an agent named {string}")]
    public async Task GivenARepositoryProjectWithAnAgent(string name)
    {
        var pushed = await Alice.PushAgentAsync(name, baseVersion: 0, project: RegistryUser.GitFolder("/home/alice/src/widget", RegistryUser.NewCommit()));
        _repositoryProject = await ProjectOfAsync(pushed);
    }

    [When("alice links the folder {string} to the repository project")]
    public async Task WhenAliceLinksTheFolder(string folder) =>
        await RecordLinkAsync(Alice.Http.PostAsJsonAsync("/v1/projects/link", new
        {
            machineId = await Alice.EnsureMachineAsync(),
            facts = RegistryUser.PlainFolder(folder),
            project = _repositoryProject.ToString(),
        }));

    [When("alice unlinks the folder {string}")]
    public async Task WhenAliceUnlinksTheFolder(string folder) =>
        await RecordLinkAsync(Alice.Http.PostAsJsonAsync("/v1/projects/unlink", new
        {
            machineId = await Alice.EnsureMachineAsync(),
            facts = RegistryUser.PlainFolder(folder),
        }));

    [Then("the link answers that the agent {string} moved")]
    public void ThenTheLinkAnswersThatTheAgentMoved(string name)
    {
        Assert.True(_link!.StatusCode == HttpStatusCode.OK, $"Expected 200, got {(int)_link.StatusCode}: {_linkBody}");
        Assert.Equal([name], _linkBody!["movedAgents"]!.AsArray().Select(a => a!.GetValue<string>()));
        Assert.Empty(_linkBody["movedSkills"]!.AsArray());
    }

    [Then("the link is refused with {int} naming the agent {string}")]
    public void ThenTheLinkIsRefused(int status, string name)
    {
        Assert.Equal(status, (int)_link!.StatusCode);
        Assert.Contains($"an agent named {name}", _linkBody!["title"]!.GetValue<string>());
    }

    [Then("alice's agent {string} is in the repository project")]
    public async Task ThenTheAgentIsInTheRepositoryProject(string name) =>
        Assert.Contains(await AgentsOfRepositoryAsync(), a => a == name);

    [Then("alice's agent {string} is not in the repository project")]
    public async Task ThenTheAgentIsNotInTheRepositoryProject(string name)
    {
        Assert.DoesNotContain(await AgentsOfRepositoryAsync(), a => a == name);
        var all = (await Alice.GetJsonAsync("/v1/agents?includeProjects=true"))["agents"]!.AsArray();
        Assert.Contains(all, a => a!["name"]!.GetValue<string>() == name && a["projectId"]!.GetValue<Guid>() != _repositoryProject);
    }

    private async Task<IReadOnlyList<string>> AgentsOfRepositoryAsync() =>
        (await Alice.GetJsonAsync($"/v1/agents?projectId={_repositoryProject}"))["agents"]!.AsArray().Select(a => a!["name"]!.GetValue<string>()).ToList();

    private async Task RecordAsync(Task<HttpResponseMessage> push)
    {
        _push = await push;
        _body = await _push.Content.ReadFromJsonAsync<JsonObject>();
    }

    private async Task RecordLinkAsync(Task<HttpResponseMessage> link)
    {
        _link = await link;
        _linkBody = await _link.Content.ReadFromJsonAsync<JsonObject>();
    }

    private static async Task<Guid> ProjectOfAsync(HttpResponseMessage pushed)
    {
        var body = await pushed.Content.ReadAsStringAsync();
        Assert.True(pushed.StatusCode == HttpStatusCode.Created, $"Expected 201, got {(int)pushed.StatusCode}: {body}");
        return JsonNode.Parse(body)!["projectId"]!.GetValue<Guid>();
    }

    private static async Task AssertStoredAsync(HttpResponseMessage response, int version)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"Expected 201, got {(int)response.StatusCode}: {body}");
        Assert.Equal(version, JsonNode.Parse(body)!["version"]!.GetValue<int>());
    }
}
