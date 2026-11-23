using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Npgsql;
using Protostar.Registry.Acceptance.Support;
using Reqnroll;
using Xunit;

namespace Protostar.Registry.Acceptance.Steps;

/// <summary>Steps for SessionAgents.feature and FindingAgents.feature: the agents a session used, and searching and reading agents.</summary>
/// <remarks>
/// Sessions are written as the Claude Code JSONL lines the parser reads: an assistant <c>tool_use</c> of the <c>Agent</c>
/// (or <c>Task</c>) tool naming a <c>subagent_type</c>. Every line is stamped with the time the step runs, so a version
/// pushed before the step counts, as does one pushed when the session ends; a later edit is dated an hour on.
/// </remarks>
[Binding]
public sealed class AgentUseSteps
{
    // A well-formed GitHub token (the prefix, then 36 characters), assembled here so the literal is not in the repository.
    private static readonly string GitHubToken = "gh" + "p_" + "abcdefghijklmnopqrstuvwxyz0123456789";

    private readonly RegistryScenario _scenario;
    private readonly Dictionary<string, Guid> _agentIds = new(StringComparer.Ordinal);
    private readonly JsonObject _folder = RegistryUser.GitFolder("/home/alice/src/demo", RegistryUser.NewCommit());
    private string? _sessionKey;
    private string? _subagentKey;
    private Guid? _projectAgentId;
    private string? _answerText;
    private JsonArray? _answer;
    private HttpResponseMessage? _bobsAnswer;
    private JsonArray? _hits;
    private JsonObject? _read;
    private string? _pushedFile;

    public AgentUseSteps(RegistryScenario scenario) => _scenario = scenario;

    private RegistryUser Alice => _scenario.User("alice");

    private string SessionKey => _sessionKey ?? throw new InvalidOperationException("alice has not backed up a session.");

    private JsonArray Answer => _answer ?? throw new InvalidOperationException("Nobody has asked which agents were used.");

    private JsonArray Hits => _hits ?? throw new InvalidOperationException("No agent search has been made in this scenario.");

    [Given("alice has pushed the agent {string}")]
    public async Task GivenAliceHasPushedTheAgent(string name) => _agentIds[name] = await PushAsync(Alice.PushAgentAsync(name));

    [Given("an hour after the session, alice pushed a second version of the agent {string}")]
    public async Task GivenAliceLaterPushedASecondVersion(string name)
    {
        Assert.Equal(2, (await PushedAsync(Alice.PushAgentAsync(name)))["version"]!.GetValue<int>());
        await DateVersionAnHourOnAsync(_agentIds[name], version: 2);
    }

    [Given("the capture hook pushed the agent {string} when the session ended")]
    public async Task GivenTheCaptureHookPushedAtSessionEnd(string name) => await GivenAliceHasPushedTheAgent(name);

    [Given("alice has pushed a personal agent {string} and an agent {string} in her project {string}")]
    public async Task GivenAPersonalAndAProjectAgent(string personal, string inProject, string project)
    {
        _agentIds[personal] = await PushAsync(Alice.PushAgentAsync(personal));
        _projectAgentId = await PushAsync(Alice.PushAgentAsync(inProject, project: _folder));
        Assert.NotEqual(_agentIds[personal], _projectAgentId);
    }

    [Given("alice has backed up a session that delegated to {string}, {string} and {string}, with the prompt {string}")]
    public async Task GivenASessionThatDelegatedTo(string first, string second, string third, string prompt)
    {
        var now = DateTimeOffset.UtcNow;
        _sessionKey = await Alice.BackUpSessionAsync(key => RegistryUser.Jsonl(
            RegistryUser.AgentCallLine(key, first, now, prompt),
            RegistryUser.AgentCallLine(key, second, now, prompt),
            RegistryUser.AgentCallLine(key, third, now, prompt)));
    }

    [Given("a subagent of that session delegated to {string}, {string} and {string}")]
    public async Task GivenASubagentDelegatedTo(string first, string second, string third)
    {
        _subagentKey = $"{SessionKey}.agent-1";
        var now = DateTimeOffset.UtcNow;
        await Alice.BackUpTranscriptAsync(_subagentKey, RegistryUser.Jsonl(
            RegistryUser.AgentCallLine(_subagentKey, first, now),
            RegistryUser.AgentCallLine(_subagentKey, second, now),
            RegistryUser.AgentCallLine(_subagentKey, third, now)), parentKey: SessionKey);
    }

    [Given("alice has backed up a session that delegated to {string} through the Task tool")]
    public async Task GivenASessionThatDelegatedThroughTask(string name)
    {
        var now = DateTimeOffset.UtcNow;
        _sessionKey = await Alice.BackUpSessionAsync(key => RegistryUser.Jsonl(RegistryUser.AgentCallLine(key, name, now, tool: "Task")));
    }

    [Given("alice has backed up a session in {string} that delegated to {string}")]
    public async Task GivenASessionInTheProjectThatDelegatedTo(string project, string name)
    {
        _sessionKey = Guid.NewGuid().ToString();
        await Alice.BackUpTranscriptAsync(_sessionKey, RegistryUser.Jsonl(RegistryUser.AgentCallLine(_sessionKey, name, DateTimeOffset.UtcNow)), project: _folder);
    }

    [Given("it was parsed by a registry from before agents were recognised")]
    public async Task GivenParsedByAnOlderRegistry()
    {
        await using var connection = new NpgsqlConnection(RegistryInfrastructure.ConnectionString(_scenario.Database));
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """UPDATE "Transcripts" SET "Summary_ParserVersion" = 2, "Summary_Agents" = '[]'::jsonb""", connection);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    [When("alice asks which agents the session used")]
    public Task WhenAliceAsksAboutTheSession() => AskAsync(SessionKey);

    [When("alice asks which agents the subagent used")]
    public Task WhenAliceAsksAboutTheSubagent() => AskAsync(_subagentKey!);

    [When("bob asks which agents alice's session used")]
    public async Task WhenBobAsks() =>
        _bobsAnswer = await _scenario.User("bob").Http.GetAsync($"/v1/transcripts/{RegistryUser.Harness}/{SessionKey}/agents");

    [Then("bob's request for the agents is refused with {int}")]
    public void ThenBobIsRefused(int status) => Assert.Equal((HttpStatusCode)status, _bobsAnswer!.StatusCode);

    [Then(@"^the (?:session|subagent) used only ""(.*)"", (\d+) times?, as her personal agent at inferred version (\d+)$")]
    public void ThenUsedOnlyAtVersion(string name, int uses, int version)
    {
        var used = Only(name, uses);
        Assert.Equal(_agentIds[name], used["agentId"]!.GetValue<Guid>());
        Assert.Null(used["projectId"]);
        Assert.Equal(version, used["version"]!.GetValue<int>());
        Assert.True(used["versionInferred"]!.GetValue<bool>());
        Assert.False(used["deleted"]!.GetValue<bool>());
    }

    [Then(@"^the session used only ""(.*)"", (\d+) times?, deleted from the registry$")]
    public void ThenUsedOnlyDeleted(string name, int uses)
    {
        var used = Only(name, uses);
        Assert.True(used["deleted"]!.GetValue<bool>());
        Assert.Null(used["agentId"]);
        Assert.Null(used["version"]);
    }

    [Then("the session's {string} is the project agent, not the personal one")]
    public void ThenTheProjectAgentWins(string name)
    {
        var used = Only(name, 1);
        Assert.Equal(_projectAgentId, used["agentId"]!.GetValue<Guid>());
        Assert.NotNull(used["projectId"]);
    }

    [Then("the session used no agents")]
    public void ThenNoAgents() => Assert.Empty(Answer);

    [Then("neither the agents the registry kept nor its answer hold {string}")]
    public async Task ThenNothingHolds(string prompt)
    {
        Assert.DoesNotContain(prompt, _answerText!);
        await using var connection = new NpgsqlConnection(RegistryInfrastructure.ConnectionString(_scenario.Database));
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""SELECT string_agg("Summary_Agents"::text, ' ') FROM "Transcripts" """, connection);
        var kept = (string)(await command.ExecuteScalarAsync())!;
        Assert.Contains("reviewer", kept);
        Assert.DoesNotContain(prompt, kept);
    }

    [Given("alice has pushed the agent {string} saying {string}")]
    public async Task GivenAliceHasPushedTheAgentSaying(string name, string text)
    {
        _pushedFile = $"---\nname: {name}\ndescription: {name} agent.\n---\n{Expand(text)}\n";
        _agentIds[name] = await PushAsync(Alice.PushAgentAsync(name, markdown: _pushedFile));
    }

    [When("alice searches her agents for {string}")]
    public Task WhenAliceSearchesHerAgents(string query) => SearchAsync("alice", query, "&kind=agent");

    [When("bob searches his agents for {string}")]
    public Task WhenBobSearchesHisAgents(string query) => SearchAsync("bob", query, "&kind=agent");

    [When("alice searches for {string} with no kind")]
    public Task WhenAliceSearchesWithNoKind(string query) => SearchAsync("alice", query, "");

    [Then("the search finds her agent {string} at version {int}")]
    public void ThenTheSearchFindsHerAgent(string name, int version)
    {
        var hit = Assert.Single(Hits)!;
        Assert.Equal("agent", hit["kind"]!.GetValue<string>());
        Assert.Equal(name, hit["name"]!.GetValue<string>());
        Assert.Equal(version, hit["version"]!.GetValue<int>());
        Assert.Equal($"{name} agent.", hit["description"]!.GetValue<string>());
        Assert.Null(hit["machineId"]);
        Assert.Null(hit["key"]);
    }

    [Then("the agent hit's snippet shows {string} and not {string}")]
    public void ThenTheSnippetShows(string shown, string hidden)
    {
        var snippet = Assert.Single(Hits)!["snippet"]!.GetValue<string>();
        Assert.True(snippet.Contains(shown, StringComparison.Ordinal), $"The snippet was: {snippet}");
        Assert.DoesNotContain(Expand(hidden), snippet);
    }

    [Then("the agent search finds nothing")]
    [Then("the search without a kind finds nothing")]
    public void ThenTheSearchFindsNothing() => Assert.Empty(Hits);

    [When("alice reads her agent {string}")]
    public async Task WhenAliceReadsHerAgent(string name) => _read = (await Alice.GetJsonAsync($"/v1/agents/{name}/content")).AsObject();

    [When("alice reads her agent {string} redacted")]
    public async Task WhenAliceReadsHerAgentRedacted(string name) =>
        _read = (await Alice.GetJsonAsync($"/v1/agents/{name}/content?redacted=true")).AsObject();

    [Then("the file is the one she pushed, hashing to its content hash")]
    public void ThenTheFileIsTheOnePushed()
    {
        var bytes = Convert.FromBase64String(_read!["content"]!.GetValue<string>());
        Assert.Equal(_pushedFile, Encoding.UTF8.GetString(bytes));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)), _read["contentHash"]!.GetValue<string>());
        Assert.False(_read["redacted"]!.GetValue<bool>());
        Assert.Equal(1, _read["version"]!.GetValue<int>());
        Assert.Equal("migrator.md", _read["path"]!.GetValue<string>());
    }

    [Then("the file shows {string} and not {string}")]
    public void ThenTheFileShows(string shown, string hidden)
    {
        var text = Encoding.UTF8.GetString(Convert.FromBase64String(_read!["content"]!.GetValue<string>()));
        Assert.Contains(shown, text);
        Assert.DoesNotContain(Expand(hidden), text);
        Assert.True(_read["redacted"]!.GetValue<bool>());
        Assert.Equal(Encoding.UTF8.GetByteCount(text), _read["size"]!.GetValue<long>());
    }

    [Then("reading version {int} of alice's agent {string} answers {int}")]
    public async Task ThenReadingVersionAnswers(int version, string name, int status)
    {
        var response = await Alice.Http.GetAsync($"/v1/agents/{name}/content?version={version}");
        Assert.Equal((HttpStatusCode)status, response.StatusCode);
        var problem = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.Equal("https://protostar.dev/problems/unknown-agent", problem["type"]!.GetValue<string>());
    }

    [Then("bob reading the agent {string} answers {int}")]
    public async Task ThenBobReadingAnswers(string name, int status) =>
        Assert.Equal((HttpStatusCode)status, (await _scenario.User("bob").Http.GetAsync($"/v1/agents/{name}/content")).StatusCode);

    private async Task SearchAsync(string login, string query, string kind) =>
        _hits = (await _scenario.User(login).GetJsonAsync($"/v1/search?q={Uri.EscapeDataString(Expand(query))}{kind}")).AsArray();

    private async Task AskAsync(string key)
    {
        var response = await Alice.Http.GetAsync($"/v1/transcripts/{RegistryUser.Harness}/{key}/agents");
        _answerText = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"Asking which agents were used answered {(int)response.StatusCode}: {_answerText}");
        var body = JsonNode.Parse(_answerText)!;
        Assert.Equal(key, body["key"]!.GetValue<string>());
        _answer = body["agents"]!.AsArray();
    }

    private JsonNode Only(string name, int uses)
    {
        var used = Assert.Single(Answer)!;
        Assert.Equal(name, used["name"]!.GetValue<string>());
        Assert.Equal(uses, used["uses"]!.GetValue<int>());
        Assert.NotNull(used["firstUsedAt"]);
        return used;
    }

    // The registry dates a version by its own clock, so a push meant to come well after the session is moved an hour on.
    private async Task DateVersionAnHourOnAsync(Guid agentId, int version)
    {
        await using var connection = new NpgsqlConnection(RegistryInfrastructure.ConnectionString(_scenario.Database));
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """UPDATE "AgentVersions" SET "CreatedAt" = "CreatedAt" + interval '1 hour' WHERE "AgentId" = @agent AND "VersionNumber" = @version""",
            connection);
        command.Parameters.AddWithValue("agent", agentId);
        command.Parameters.AddWithValue("version", version);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private static async Task<Guid> PushAsync(Task<HttpResponseMessage> push) => (await PushedAsync(push))["agentId"]!.GetValue<Guid>();

    private static async Task<JsonNode> PushedAsync(Task<HttpResponseMessage> push)
    {
        var response = await push;
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"The push answered {(int)response.StatusCode}: {body}");
        return JsonNode.Parse(body)!;
    }

    private static string Expand(string text) => text.Replace("{github-token}", GitHubToken, StringComparison.Ordinal);
}
