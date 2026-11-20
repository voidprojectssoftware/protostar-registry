using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Protostar.Registry.Acceptance.Support;
using Reqnroll;
using Xunit;

namespace Protostar.Registry.Acceptance.Steps;

/// <summary>Steps for SkillPushRefusals.feature: pushes with a base version against a skill that moved on or went away.</summary>
[Binding]
public sealed class SkillPushSteps
{
    private readonly RegistryScenario _scenario;
    private HttpResponseMessage? _push;
    private JsonObject? _body;

    public SkillPushSteps(RegistryScenario scenario) => _scenario = scenario;

    private JsonObject Body => _body ?? throw new InvalidOperationException("No push has been made in this scenario.");

    [Given("{word} has pushed the skill {string} twice, reaching version {int}")]
    public async Task GivenHasPushedTheSkillTwice(string login, string name, int version)
    {
        var user = _scenario.User(login);
        await AssertStoredAsync(await user.PushSkillAsync(name, baseVersion: 0), 1);
        await AssertStoredAsync(await user.PushSkillAsync(name, baseVersion: 1), version);
    }

    [Given("{word} has deleted the skill {string}")]
    public async Task GivenHasDeletedTheSkill(string login, string name) =>
        Assert.Equal(HttpStatusCode.NoContent, (await _scenario.User(login).Http.DeleteAsync($"/v1/skills/{name}")).StatusCode);

    [Given("{word} has renamed the skill {string} to {string}")]
    public async Task GivenHasRenamedTheSkill(string login, string name, string newName) =>
        Assert.Equal(HttpStatusCode.OK, (await _scenario.User(login).Http.PostAsJsonAsync($"/v1/skills/{name}/rename", new { newName })).StatusCode);

    [When("{word} pushes different content for {string} based on version {int}")]
    public async Task WhenPushesDifferentContent(string login, string name, int baseVersion)
    {
        _push = await _scenario.User(login).PushSkillAsync(name, baseVersion);
        _body = await _push.Content.ReadFromJsonAsync<JsonObject>();
    }

    [Then("the push is refused with {int} and problem type {string}")]
    public void ThenThePushIsRefused(int status, string type)
    {
        Assert.Equal(status, (int)_push!.StatusCode);
        Assert.Equal(type, Body["type"]!.GetValue<string>());
    }

    [Then("the problem says the current version is {int}")]
    public void ThenTheProblemSaysTheCurrentVersionIs(int version) =>
        Assert.Equal(version, Body["currentVersion"]!.GetValue<int>());

    [Then("the problem says the last version was {int}")]
    public void ThenTheProblemSaysTheLastVersionWas(int version) =>
        Assert.Equal(version, Body["lastVersion"]!.GetValue<int>());

    [Then("the problem says the skill was renamed to {string}")]
    public void ThenTheProblemSaysTheSkillWasRenamedTo(string name) =>
        Assert.Equal(name, Body["renamedTo"]!.GetValue<string>());

    [Then("the push is stored as version {int}")]
    public Task ThenThePushIsStoredAsVersion(int version) => AssertStoredAsync(_push!, version);

    [Then("{word}'s skill {string} is still at version {int}")]
    public async Task ThenTheSkillIsStillAtVersion(string login, string name, int version)
    {
        var skill = (await _scenario.User(login).GetJsonAsync("/v1/skills"))["skills"]!.AsArray()
            .Single(s => s!["name"]!.GetValue<string>() == name)!;
        Assert.Equal(version, skill["version"]!.GetValue<int>());
    }

    [Then("{word} has no skill named {string}")]
    public async Task ThenHasNoSkillNamed(string login, string name)
    {
        var user = _scenario.User(login);
        var skills = (await user.GetJsonAsync("/v1/skills"))["skills"]!.AsArray();
        Assert.DoesNotContain(skills, s => s!["name"]!.GetValue<string>() == name);
        Assert.Equal(HttpStatusCode.NotFound, (await user.Http.GetAsync($"/v1/skills/{name}/files")).StatusCode);
    }

    private static async Task AssertStoredAsync(HttpResponseMessage response, int version)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"Expected 201, got {(int)response.StatusCode}: {body}");
        Assert.Equal(version, JsonNode.Parse(body)!["version"]!.GetValue<int>());
    }
}
