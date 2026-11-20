using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Protostar.Registry.Acceptance.Support;
using Reqnroll;
using Xunit;

namespace Protostar.Registry.Acceptance.Steps;

/// <summary>Steps for Capabilities.feature: the capability list in /v1/meta, and each listed feature used once.</summary>
/// <remarks>
/// Each capability has one check below that uses the behavior its name promises, not just its route: a name that stays
/// listed after its feature was removed fails here. The checks are deliberately small; the features' own scenarios
/// cover them in depth.
/// </remarks>
[Binding]
public sealed class CapabilitySteps
{
    // A well-formed fake, assembled here so the literal is not in the repository.
    private static readonly string GitHubToken = "gh" + "p_" + "abcdefghijklmnopqrstuvwxyz0123456789";

    private readonly RegistryScenario _scenario;
    private readonly Dictionary<string, Func<RegistryUser, Task>> _checks;
    private JsonObject? _meta;

    public CapabilitySteps(RegistryScenario scenario)
    {
        _scenario = scenario;
        _checks = new(StringComparer.Ordinal)
        {
            ["skills"] = UseSkillsAsync,
            ["stale-skill-push"] = UseStaleSkillPushAsync,
            ["project-skills"] = UseProjectSkillsAsync,
            ["skill-rename"] = UseSkillRenameAsync,
            ["skill-delete"] = UseSkillDeleteAsync,
            ["skill-tombstones"] = UseSkillTombstonesAsync,
            ["sessions"] = UseSessionsAsync,
            ["transcript-lookup"] = UseTranscriptLookupAsync,
            ["transcript-prefix-lookup"] = UseTranscriptPrefixLookupAsync,
            ["session-skills"] = UseSessionSkillsAsync,
            ["redacted-read"] = UseRedactedReadAsync,
            ["projects"] = UseProjectsAsync,
            ["memory"] = UseMemoryAsync,
            ["memory-content"] = UseMemoryContentAsync,
            ["memory-base-versions"] = UseMemoryBaseVersionsAsync,
            ["search"] = UseSearchAsync,
            ["memory-redacted-read"] = UseMemoryRedactedReadAsync,
        };
    }

    private IReadOnlyList<string> Capabilities =>
        (_meta ?? throw new InvalidOperationException("The meta has not been read in this scenario."))["capabilities"]!
            .AsArray().Select(c => c!.GetValue<string>()).ToList();

    [When("a client asks the registry for its meta")]
    public async Task WhenAClientAsksForTheMeta()
    {
        using var http = _scenario.CreateAnonymousClient();
        var response = await http.GetAsync("/v1/meta");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        _meta = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
    }

    [Given("the meta lists the capability {string}")]
    public async Task GivenTheMetaListsTheCapability(string capability)
    {
        await WhenAClientAsksForTheMeta();
        Assert.Contains(capability, Capabilities);
    }

    [Then("the meta lists API major {int}")]
    public void ThenTheMetaListsApiMajor(int major) =>
        Assert.Contains(major, _meta!["apiMajors"]!.AsArray().Select(m => m!.GetValue<int>()));

    [Then("the meta lists exactly these capabilities, in this order")]
    public void ThenTheMetaListsExactlyTheseCapabilities(DataTable table) =>
        Assert.Equal(table.Rows.Select(row => row["capability"]), Capabilities);

    [Then("every capability the meta lists has a check of its feature")]
    public void ThenEveryCapabilityHasACheck() =>
        Assert.Equal(Capabilities.Order(StringComparer.Ordinal), _checks.Keys.Order(StringComparer.Ordinal));

    [Then("{word} can use the feature behind {string}")]
    public Task ThenCanUseTheFeatureBehind(string login, string capability) =>
        _checks.TryGetValue(capability, out var check)
            ? check(_scenario.User(login))
            : throw new InvalidOperationException($"No check for the capability \"{capability}\": add one to CapabilitySteps.");

    private static async Task UseSkillsAsync(RegistryUser user)
    {
        await AssertStatusAsync(user.PushSkillAsync("probe"), HttpStatusCode.Created);
        var listed = (await user.GetJsonAsync("/v1/skills"))["skills"]!.AsArray();
        Assert.Contains(listed, s => s!["name"]!.GetValue<string>() == "probe");
        var files = (await user.GetJsonAsync("/v1/skills/probe/files"))["files"]!.AsArray();
        Assert.Contains(files, f => f!["path"]!.GetValue<string>() == "SKILL.md");
    }

    private static async Task UseStaleSkillPushAsync(RegistryUser user)
    {
        await AssertStatusAsync(user.PushSkillAsync("probe", baseVersion: 0), HttpStatusCode.Created);
        await AssertStatusAsync(user.PushSkillAsync("probe", baseVersion: 1), HttpStatusCode.Created);
        var stale = await AssertStatusAsync(user.PushSkillAsync("probe", baseVersion: 1), HttpStatusCode.Conflict);
        Assert.Equal("https://protostar.dev/problems/stale-skill-version", stale["type"]!.GetValue<string>());
        Assert.Equal(2, stale["currentVersion"]!.GetValue<int>());
    }

    private static async Task UseProjectSkillsAsync(RegistryUser user)
    {
        var folder = RegistryUser.GitFolder("/home/alice/src/demo", RegistryUser.NewCommit());
        var pushed = await AssertStatusAsync(user.PushSkillAsync("probe", project: folder), HttpStatusCode.Created);
        var projectId = pushed["projectId"]!.GetValue<Guid>();

        Assert.Empty((await user.GetJsonAsync("/v1/skills"))["skills"]!.AsArray());
        var listed = (await user.GetJsonAsync($"/v1/skills?projectId={projectId}"))["skills"]!.AsArray();
        Assert.Equal(projectId, Assert.Single(listed)!["projectId"]!.GetValue<Guid>());
    }

    private static async Task UseSkillRenameAsync(RegistryUser user)
    {
        await AssertStatusAsync(user.PushSkillAsync("probe"), HttpStatusCode.Created);
        var renamed = await AssertStatusAsync(user.Http.PostAsJsonAsync("/v1/skills/probe/rename", new { newName = "renamed" }), HttpStatusCode.OK);
        Assert.Equal("renamed", renamed["name"]!.GetValue<string>());
        await AssertStatusAsync(user.Http.GetAsync("/v1/skills/renamed/files"), HttpStatusCode.OK);
    }

    private static async Task UseSkillDeleteAsync(RegistryUser user)
    {
        await AssertStatusAsync(user.PushSkillAsync("probe"), HttpStatusCode.Created);
        Assert.Equal(HttpStatusCode.NoContent, (await user.Http.DeleteAsync("/v1/skills/probe")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await user.Http.GetAsync("/v1/skills/probe/files")).StatusCode);
    }

    private static async Task UseSkillTombstonesAsync(RegistryUser user)
    {
        await AssertStatusAsync(user.PushSkillAsync("probe", baseVersion: 0), HttpStatusCode.Created);
        Assert.Equal(HttpStatusCode.NoContent, (await user.Http.DeleteAsync("/v1/skills/probe")).StatusCode);

        var removed = Assert.Single((await user.GetJsonAsync("/v1/skills"))["removed"]!.AsArray())!;
        Assert.Equal("probe", removed["name"]!.GetValue<string>());
        Assert.Equal(1, removed["lastVersion"]!.GetValue<int>());
        var refused = await AssertStatusAsync(user.PushSkillAsync("probe", baseVersion: 1), HttpStatusCode.Conflict);
        Assert.Equal("https://protostar.dev/problems/skill-deleted", refused["type"]!.GetValue<string>());
    }

    private static async Task UseSessionsAsync(RegistryUser user)
    {
        var key = await user.BackUpSessionAsync("hello from the capability check");
        Assert.NotNull(await user.GetJsonAsync($"/v1/transcripts/{RegistryUser.Harness}/{key}"));
        var listed = (await user.GetJsonAsync("/v1/transcripts")).AsArray();
        Assert.Contains(listed, t => t!["key"]!.GetValue<string>() == key);
        var content = await user.Http.GetStringAsync($"/v1/transcripts/{RegistryUser.Harness}/{key}/content");
        Assert.Contains("hello from the capability check", content);
    }

    private static async Task UseTranscriptLookupAsync(RegistryUser user)
    {
        var key = await user.BackUpSessionAsync("hello");
        var unknown = Guid.NewGuid().ToString();
        var response = await user.Http.PostAsJsonAsync("/v1/transcripts/lookup", new
        {
            transcripts = new[] { new { harness = RegistryUser.Harness, key }, new { harness = RegistryUser.Harness, key = unknown } },
        });
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"Lookup answered {(int)response.StatusCode}: {body}");

        var answers = JsonNode.Parse(body)!.AsArray();
        Assert.Equal(2, answers.Count);
        Assert.True(answers[0]!["known"]!.GetValue<bool>());
        Assert.False(answers[1]!["known"]!.GetValue<bool>());
    }

    private static async Task UseTranscriptPrefixLookupAsync(RegistryUser user)
    {
        var key = await user.BackUpSessionAsync("hello");
        await user.BackUpSessionAsync("another");
        var found = (await user.GetJsonAsync($"/v1/transcripts?keyPrefix={key[..8].ToUpperInvariant()}&limit=11")).AsArray();
        Assert.Equal(key, Assert.Single(found)!["key"]!.GetValue<string>());
        await AssertStatusAsync(user.Http.GetAsync("/v1/transcripts?keyPrefix=abc"), HttpStatusCode.BadRequest);
    }

    private static async Task UseSessionSkillsAsync(RegistryUser user)
    {
        await AssertStatusAsync(user.PushSkillAsync("probe"), HttpStatusCode.Created);
        var usedAt = DateTimeOffset.UtcNow;
        var key = await user.BackUpSessionAsync(k => RegistryUser.Jsonl(RegistryUser.SkillCallLine(k, "probe", usedAt)));
        var used = Assert.Single((await user.GetJsonAsync($"/v1/transcripts/{RegistryUser.Harness}/{key}/skills"))["skills"]!.AsArray())!;
        Assert.Equal("probe", used["name"]!.GetValue<string>());
        Assert.Equal(1, used["version"]!.GetValue<int>());
        Assert.True(used["versionInferred"]!.GetValue<bool>());
    }

    private static async Task UseRedactedReadAsync(RegistryUser user)
    {
        var key = await user.BackUpSessionAsync($"deploy it with my token {GitHubToken} please");
        var read = await user.Http.GetStringAsync($"/v1/transcripts/{RegistryUser.Harness}/{key}/content?redacted=true");
        Assert.Contains("[redacted:github-token]", read);
        Assert.DoesNotContain(GitHubToken, read);
    }

    private static async Task UseProjectsAsync(RegistryUser user)
    {
        var machineId = await user.EnsureMachineAsync();
        var home = RegistryUser.GitFolder("/home/alice/src/demo", RegistryUser.NewCommit());
        var projectId = await user.PushMemoryAsync(home, "MEMORY.md", "a project of its own");

        var resolved = await AssertStatusAsync(user.Http.PostAsJsonAsync("/v1/projects/resolve", new { machineId, facts = home }), HttpStatusCode.OK);
        Assert.Equal(projectId, resolved["projectId"]!.GetValue<Guid>());

        var other = RegistryUser.GitFolder("/home/alice/src/other", RegistryUser.NewCommit());
        var linked = await AssertStatusAsync(
            user.Http.PostAsJsonAsync("/v1/projects/link", new { machineId, facts = other, project = projectId.ToString() }), HttpStatusCode.OK);
        Assert.Equal(projectId, linked["project"]!["projectId"]!.GetValue<Guid>());
        await AssertStatusAsync(user.Http.PostAsJsonAsync("/v1/projects/unlink", new { machineId, facts = other }), HttpStatusCode.OK);
    }

    private static async Task UseMemoryAsync(RegistryUser user)
    {
        var projectId = await user.PushMemoryAsync(RegistryUser.GitFolder("/home/alice/src/demo", RegistryUser.NewCommit()), "MEMORY.md", "remember this");
        var projects = (await user.GetJsonAsync("/v1/memory")).AsArray();
        Assert.Equal(projectId, Assert.Single(projects)!["projectId"]!.GetValue<Guid>());
        var files = (await user.GetJsonAsync($"/v1/memory/{projectId}"))["files"]!.AsArray();
        Assert.Equal("MEMORY.md", Assert.Single(files)!["path"]!.GetValue<string>());
    }

    private static async Task UseMemoryContentAsync(RegistryUser user)
    {
        var projectId = await user.PushMemoryAsync(RegistryUser.GitFolder("/home/alice/src/demo", RegistryUser.NewCommit()), "MEMORY.md", "remember this");
        var file = Assert.Single((await user.GetJsonAsync($"/v1/memory/{projectId}/content"))["files"]!.AsArray())!;
        Assert.Equal("remember this", Encoding.UTF8.GetString(Convert.FromBase64String(file["content"]!.GetValue<string>())));
    }

    private static async Task UseMemoryBaseVersionsAsync(RegistryUser user)
    {
        var folder = RegistryUser.GitFolder("/home/alice/src/demo", RegistryUser.NewCommit());
        var projectId = await user.PushMemoryAsync(folder, "MEMORY.md", "first");
        var machineId = await user.EnsureMachineAsync();
        await AssertStatusAsync(user.Http.PostAsJsonAsync("/v1/memory", new
        {
            machineId,
            project = folder,
            files = new[] { new { path = "MEMORY.md", content = Encoding.UTF8.GetBytes("second"), baseVersion = 1 } },
        }), HttpStatusCode.OK);

        var file = Assert.Single((await user.GetJsonAsync($"/v1/memory/{projectId}/content"))["files"]!.AsArray())!;
        var second = file["history"]!.AsArray().Single(h => h!["version"]!.GetValue<int>() == 2)!;
        Assert.Equal(1, second["base"]!.GetValue<int>());
        Assert.NotNull(file["deletedOn"]);
    }

    private static async Task UseSearchAsync(RegistryUser user)
    {
        var key = await user.BackUpSessionAsync("the zebra crossing is closed");
        var hits = (await user.GetJsonAsync("/v1/search?q=zebra")).AsArray();
        Assert.Equal(key, Assert.Single(hits)!["key"]!.GetValue<string>());
    }

    private static async Task UseMemoryRedactedReadAsync(RegistryUser user)
    {
        var projectId = await user.PushMemoryAsync(
            RegistryUser.GitFolder("/home/alice/src/demo", RegistryUser.NewCommit()), "MEMORY.md", $"the token is {GitHubToken}");
        var file = Assert.Single((await user.GetJsonAsync($"/v1/memory/{projectId}/content?redacted=true"))["files"]!.AsArray())!;
        var read = Encoding.UTF8.GetString(Convert.FromBase64String(file["content"]!.GetValue<string>()));
        Assert.Equal("the token is [redacted:github-token]", read);
    }

    private static async Task<JsonObject> AssertStatusAsync(Task<HttpResponseMessage> request, HttpStatusCode expected)
    {
        var response = await request;
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"Expected {(int)expected}, got {(int)response.StatusCode}: {body}");
        return body.Length == 0 ? [] : JsonNode.Parse(body)!.AsObject();
    }
}
