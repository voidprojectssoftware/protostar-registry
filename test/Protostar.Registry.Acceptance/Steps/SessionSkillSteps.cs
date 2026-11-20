using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Protostar.Registry.Acceptance.Support;
using Protostar.Registry.Api.Sessions;
using Reqnroll;
using Xunit;

namespace Protostar.Registry.Acceptance.Steps;

/// <summary>Steps for SessionSkills.feature: sessions that invoke skills, and what the registry says they used.</summary>
/// <remarks>
/// Sessions are written as the Claude Code JSONL lines the parser reads: an assistant <c>tool_use</c> of the
/// <c>Skill</c> tool, or the user's slash command followed by the skill's injected content. Every use is stamped with
/// the time the step runs, so a version pushed before the step counts, as does one the capture hook pushes just after
/// it; a later edit is dated an hour on, past the grace.
/// </remarks>
[Binding]
public sealed class SessionSkillSteps
{
    private readonly RegistryScenario _scenario;
    private readonly Dictionary<string, Guid> _skillIds = new(StringComparer.Ordinal);
    private readonly JsonObject _folder = RegistryUser.GitFolder("/home/alice/src/demo", RegistryUser.NewCommit());
    private string? _sessionKey;
    private string? _subagentKey;
    private Guid? _projectSkillId;
    private string? _answerText;
    private JsonArray? _answer;
    private HttpResponseMessage? _bobsAnswer;

    public SessionSkillSteps(RegistryScenario scenario) => _scenario = scenario;

    private RegistryUser Alice => _scenario.User("alice");

    private string SessionKey => _sessionKey ?? throw new InvalidOperationException("alice has not backed up a session.");

    private JsonArray Answer => _answer ?? throw new InvalidOperationException("Nobody has asked which skills were used.");

    [Given("alice has pushed the skills {string} and {string}")]
    public async Task GivenAliceHasPushedTheSkills(string first, string second)
    {
        _skillIds[first] = await PushAsync(first);
        _skillIds[second] = await PushAsync(second);
    }

    [Given("an hour after the session, alice pushed a second version of {string}")]
    public async Task GivenAliceLaterPushedASecondVersion(string name)
    {
        Assert.Equal(2, (await PushedAsync(Alice.PushSkillAsync(name)))["version"]!.GetValue<int>());
        await DateVersionsAnHourOnAsync(_skillIds[name], version: 2);
    }

    [Given("an hour after the session, alice pushed the skills {string} and {string}")]
    public async Task GivenAliceLaterPushedTheSkills(string first, string second)
    {
        await GivenAliceHasPushedTheSkills(first, second);
        await DateVersionsAnHourOnAsync(_skillIds[first], version: 1);
        await DateVersionsAnHourOnAsync(_skillIds[second], version: 1);
    }

    [Given(@"^the capture hook pushed (?:her edited )?""(.*)"" as version (\d+) just after the use$")]
    public async Task GivenTheCaptureHookPushed(string name, int version)
    {
        var pushed = await PushedAsync(Alice.PushSkillAsync(name));
        Assert.Equal(version, pushed["version"]!.GetValue<int>());
        _skillIds[name] = pushed["skillId"]!.GetValue<Guid>();
    }

    [Given("alice has since deleted the skill {string}")]
    public async Task GivenAliceHasDeletedTheSkill(string name) =>
        Assert.Equal(HttpStatusCode.NoContent, (await Alice.Http.DeleteAsync($"/v1/skills/{name}")).StatusCode);

    [Given("alice has backed up a session that used {string} twice and {string} once, passing arguments {string}")]
    public async Task GivenASessionThatUsedTwiceAndOnce(string twice, string once, string args)
    {
        var now = DateTimeOffset.UtcNow;
        _sessionKey = await Alice.BackUpSessionAsync(key => RegistryUser.Jsonl(
            RegistryUser.SkillCallLine(key, twice, now, args),
            RegistryUser.SkillCallLine(key, once, now, args),
            RegistryUser.SkillCallLine(key, twice, now, args)));
    }

    [Given("alice has backed up a session that used {string}")]
    public async Task GivenASessionThatUsed(string name)
    {
        var now = DateTimeOffset.UtcNow;
        _sessionKey = await Alice.BackUpSessionAsync(key => RegistryUser.Jsonl(RegistryUser.SkillCallLine(key, name, now)));
    }

    [Given("a subagent of that session used {string}")]
    public async Task GivenASubagentUsed(string name)
    {
        _subagentKey = $"{SessionKey}.agent-1";
        await Alice.BackUpTranscriptAsync(_subagentKey, RegistryUser.Jsonl(RegistryUser.SkillCallLine(_subagentKey, name, DateTimeOffset.UtcNow)), parentKey: SessionKey);
    }

    [Given("alice has backed up a session in which she typed {string} and {string}")]
    public async Task GivenASessionInWhichSheTyped(string skillCommand, string builtInCommand)
    {
        var at = DateTimeOffset.UtcNow.ToString("O");
        _sessionKey = await Alice.BackUpSessionAsync(key => RegistryUser.Jsonl(
            UserLine(key, at, Command(skillCommand)),
            UserLine(key, at, new JsonArray(new JsonObject
            {
                ["type"] = "text",
                ["text"] = $"Base directory for this skill: /home/alice/.claude/skills/{skillCommand.TrimStart('/')}\n\n# Review\n",
            }), isMeta: true),
            UserLine(key, at, Command(builtInCommand)),
            UserLine(key, at, "<local-command-stdout></local-command-stdout>")));
    }

    [Given("alice has pushed a personal skill {string} and a skill {string} in her project {string}")]
    public async Task GivenAPersonalAndAProjectSkill(string personal, string inProject, string project)
    {
        _skillIds[personal] = await PushAsync(personal);
        var pushed = await PushedAsync(Alice.PushSkillAsync(inProject, project: _folder));
        _projectSkillId = pushed["skillId"]!.GetValue<Guid>();
        Assert.NotEqual(_skillIds[personal], _projectSkillId);
    }

    [Given("alice has backed up a session in {string} that used {string}")]
    public async Task GivenASessionInTheProjectThatUsed(string project, string name)
    {
        _sessionKey = Guid.NewGuid().ToString();
        await Alice.BackUpTranscriptAsync(_sessionKey, RegistryUser.Jsonl(RegistryUser.SkillCallLine(_sessionKey, name, DateTimeOffset.UtcNow)), project: _folder);
    }

    [Given("it was parsed by a registry from before skills were recognised")]
    public async Task GivenParsedByAnOlderRegistry()
    {
        await using var connection = new NpgsqlConnection(RegistryInfrastructure.ConnectionString(_scenario.Database));
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """UPDATE "Transcripts" SET "Summary_ParserVersion" = 1, "Summary_Skills" = '[]'::jsonb""", connection);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    [When("a reprocessing pass runs")]
    public async Task WhenAReprocessingPassRuns()
    {
        await using var scope = _scenario.Factory.Services.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<TranscriptReprocessor>().RunAsync(CancellationToken.None);
        Assert.Equal(1, result.Summaries);
        Assert.Equal(0, result.Failed);
    }

    [When("alice asks which skills the session used")]
    public Task WhenAliceAsksAboutTheSession() => AskAsync(SessionKey);

    [When("alice asks which skills the subagent used")]
    public Task WhenAliceAsksAboutTheSubagent() => AskAsync(_subagentKey!);

    [When("bob asks which skills alice's session used")]
    public async Task WhenBobAsks() =>
        _bobsAnswer = await _scenario.User("bob").Http.GetAsync($"/v1/transcripts/{RegistryUser.Harness}/{SessionKey}/skills");

    [Then("bob is refused with {int}")]
    public void ThenBobIsRefused(int status) => Assert.Equal((HttpStatusCode)status, _bobsAnswer!.StatusCode);

    [Then(@"^the (?:session|subagent) used ""(.*)"" (\d+) times?, matched to her skill at inferred version (\d+)$")]
    public void ThenUsedAtVersion(string name, int uses, int version)
    {
        var used = Used(name, uses);
        Assert.Equal(_skillIds[name], used["skillId"]!.GetValue<Guid>());
        Assert.Equal(version, used["version"]!.GetValue<int>());
        Assert.True(used["versionInferred"]!.GetValue<bool>());
        Assert.False(used["deleted"]!.GetValue<bool>());
    }

    [Then(@"^the session used ""(.*)"" (\d+) times?, matched to her skill with no version$")]
    public void ThenUsedWithNoVersion(string name, int uses)
    {
        var used = Used(name, uses);
        Assert.Equal(_skillIds[name], used["skillId"]!.GetValue<Guid>());
        Assert.Null(used["version"]);
        Assert.False(used["versionInferred"]!.GetValue<bool>());
    }

    [Then(@"^the session used ""(.*)"" (\d+) times?, matched to no skill$")]
    public void ThenUsedMatchedToNoSkill(string name, int uses)
    {
        var used = Used(name, uses);
        Assert.Null(used["skillId"]);
        Assert.Null(used["version"]);
        Assert.False(used["deleted"]!.GetValue<bool>());
    }

    [Then(@"^the session used ""(.*)"" (\d+) times?, deleted from the registry$")]
    public void ThenUsedDeleted(string name, int uses)
    {
        var used = Used(name, uses);
        Assert.True(used["deleted"]!.GetValue<bool>());
        Assert.Null(used["skillId"]);
        Assert.Null(used["version"]);
    }

    [Then("the session's {string} is the personal skill, not the project's")]
    public void ThenThePersonalSkillWins(string name) =>
        Assert.Equal(_skillIds[name], Used(name, 1)["skillId"]!.GetValue<Guid>());

    [Then("the session used no skills")]
    public void ThenNoSkills() => Assert.Empty(Answer);

    [Then(@"^the (session|subagent) used no other skills$")]
    public void ThenNoOtherSkills(string _) => Assert.Single(Answer);

    [Then("neither the skills the registry kept nor its answer hold {string}")]
    public async Task ThenNothingHolds(string args)
    {
        Assert.DoesNotContain(args, _answerText!);
        await using var connection = new NpgsqlConnection(RegistryInfrastructure.ConnectionString(_scenario.Database));
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""SELECT "Summary_Skills"::text FROM "Transcripts" """, connection);
        var kept = (string)(await command.ExecuteScalarAsync())!;
        Assert.Contains("deploy", kept);
        Assert.DoesNotContain(args, kept);
    }

    private async Task AskAsync(string key)
    {
        var response = await Alice.Http.GetAsync($"/v1/transcripts/{RegistryUser.Harness}/{key}/skills");
        _answerText = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"Asking which skills were used answered {(int)response.StatusCode}: {_answerText}");
        var body = JsonNode.Parse(_answerText)!;
        Assert.Equal(key, body["key"]!.GetValue<string>());
        _answer = body["skills"]!.AsArray();
    }

    private JsonNode Used(string name, int uses)
    {
        var used = Assert.Single(Answer, s => s!["name"]!.GetValue<string>() == name)!;
        Assert.Equal(uses, used["uses"]!.GetValue<int>());
        Assert.NotNull(used["firstUsedAt"]);
        return used;
    }

    // The registry dates a version by its own clock, so a push meant to come well after the session (a later edit,
    // outside the capture hook's grace) is moved an hour on rather than waited for.
    private async Task DateVersionsAnHourOnAsync(Guid skillId, int version)
    {
        await using var connection = new NpgsqlConnection(RegistryInfrastructure.ConnectionString(_scenario.Database));
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """UPDATE "SkillVersions" SET "CreatedAt" = "CreatedAt" + interval '1 hour' WHERE "SkillId" = @skill AND "VersionNumber" = @version""",
            connection);
        command.Parameters.AddWithValue("skill", skillId);
        command.Parameters.AddWithValue("version", version);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private async Task<Guid> PushAsync(string name) => (await PushedAsync(Alice.PushSkillAsync(name)))["skillId"]!.GetValue<Guid>();

    private static async Task<JsonNode> PushedAsync(Task<HttpResponseMessage> push)
    {
        var response = await push;
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"The push answered {(int)response.StatusCode}: {body}");
        return JsonNode.Parse(body)!;
    }

    private static string Command(string command) =>
        $"<command-message>{command.TrimStart('/')}</command-message>\n<command-name>{command}</command-name>";

    private static JsonObject UserLine(string key, string at, JsonNode content, bool isMeta = false)
    {
        var line = new JsonObject
        {
            ["type"] = "user",
            ["sessionId"] = key,
            ["timestamp"] = at,
            ["message"] = new JsonObject { ["role"] = "user", ["content"] = content },
        };
        if (isMeta)
            line["isMeta"] = true;
        return line;
    }
}
