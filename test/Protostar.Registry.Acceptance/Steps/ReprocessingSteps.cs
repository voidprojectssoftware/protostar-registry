using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Protostar.Registry.Acceptance.Support;
using Protostar.Registry.Api.Search;
using Protostar.Registry.Api.Sessions;
using Reqnroll;
using Xunit;

namespace Protostar.Registry.Acceptance.Steps;

/// <summary>Steps for Reprocessing.feature: summaries an older registry built are rebuilt, with nothing hidden and no append lost.</summary>
/// <remarks>
/// To act while the pass is mid-flight, a step locks rows (<c>SELECT ... FOR UPDATE</c> in an open transaction): the
/// session's, which holds the pass at its final save, or the session's search entries, which holds it while it reindexes
/// (after it has read the session, before it saves). Reads are not blocked by a row lock, so listings still answer.
/// Steps then wait until Postgres reports the expected number of backends waiting on a lock in this scenario's database,
/// which is the pass (or an append) blocked at its save: the thing itself, not a sleep.
/// </remarks>
[Binding]
public sealed class ReprocessingSteps : IAsyncDisposable
{
    private const string StalePrompt = "stale summary from an older parser";
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(60);

    private readonly RegistryScenario _scenario;
    private readonly Dictionary<string, string> _keysByPrompt = new(StringComparer.Ordinal);
    private string? _heldKey;
    private long _lengthBefore;
    private NpgsqlConnection? _holder;
    private NpgsqlTransaction? _hold;
    private int _waiting;
    private Task<TranscriptReprocessResult>? _pass;
    private Task<HttpResponseMessage>? _append;

    public ReprocessingSteps(RegistryScenario scenario) => _scenario = scenario;

    private RegistryUser Alice => _scenario.User("alice");

    private string OnlyKey => Assert.Single(_keysByPrompt).Value;

    [Given("alice has backed up sessions in which she typed {string} and {string}")]
    public async Task GivenAliceHasBackedUpSessions(string first, string second)
    {
        _keysByPrompt[first] = await Alice.BackUpSessionAsync(first);
        _keysByPrompt[second] = await Alice.BackUpSessionAsync(second);
    }

    [Given("alice has backed up a session in which she typed {string}")]
    public async Task GivenAliceHasBackedUpASession(string prompt)
    {
        _keysByPrompt[prompt] = await Alice.BackUpSessionAsync(prompt);
        _lengthBefore = (await GetSessionAsync(_keysByPrompt[prompt]))["length"]!.GetValue<long>();
    }

    [Given("both were processed by an older registry, which got their first prompt wrong")]
    [Given("it was processed by an older registry, which got its first prompt wrong")]
    public async Task GivenProcessedByAnOlderRegistry()
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(
            """UPDATE "Transcripts" SET "Summary_ParserVersion" = @version, "Summary_FirstPrompt" = @stale""", connection);
        command.Parameters.AddWithValue("version", TranscriptSummary.CurrentParserVersion - 1);
        command.Parameters.AddWithValue("stale", StalePrompt);
        Assert.Equal(_keysByPrompt.Count, await command.ExecuteNonQueryAsync());

        foreach (var key in _keysByPrompt.Values)
            Assert.Equal(StalePrompt, (await GetSessionAsync(key))["firstPrompt"]!.GetValue<string>());
    }

    [Given("its search entries were made by an older index version")]
    public async Task GivenItsSearchEntriesWereMadeByAnOlderIndexVersion()
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(
            """UPDATE "SearchEntries" SET "IndexVersion" = @version WHERE "TranscriptId" IS NOT NULL""", connection);
        command.Parameters.AddWithValue("version", SearchEntry.CurrentIndexVersion - 1);
        Assert.True(await command.ExecuteNonQueryAsync() > 0, "The session has no search entries to age.");
    }

    [Given("the test holds the session {string}")]
    public Task GivenTheTestHoldsTheSessionTyped(string prompt) => HoldAsync(_keysByPrompt[prompt], """
        SELECT count(*) FROM (SELECT 1 FROM "Transcripts" WHERE "Key" = @key FOR UPDATE) held
        """);

    [Given("the test holds the session")]
    public Task GivenTheTestHoldsTheSession() => GivenTheTestHoldsTheSessionTyped(_keysByPrompt.Single().Key);

    [Given("the test holds the session's search entries")]
    public Task GivenTheTestHoldsTheSessionsSearchEntries() => HoldAsync(OnlyKey, """
        SELECT count(*) FROM (
            SELECT 1 FROM "SearchEntries" e JOIN "Transcripts" t ON t."Id" = e."TranscriptId" WHERE t."Key" = @key FOR UPDATE OF e) held
        """);

    [When("a reprocessing pass starts and is waiting to save that session")]
    [When("a reprocessing pass is waiting to reindex the session")]
    [When("a reprocessing pass is waiting to save the session")]
    [When("a reprocessing pass is waiting to save the session too")]
    public Task WhenAReprocessingPassIsWaiting()
    {
        _pass = Task.Run(async () =>
        {
            await using var scope = _scenario.Factory.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<TranscriptReprocessor>().RunAsync(CancellationToken.None);
        });
        return WaitForAnotherWaiterAsync(_pass);
    }

    [When("alice appending {string} is waiting to save the session")]
    [When("alice appending {string} is waiting to save the session too")]
    public Task WhenAliceAppendingIsWaiting(string prompt)
    {
        _append = Alice.AppendPromptAsync(OnlyKey, prompt, 1, _lengthBefore);
        return WaitForAnotherWaiterAsync(_append);
    }

    [When("alice appends {string} to the session")]
    public async Task WhenAliceAppendsToTheSession(string prompt)
    {
        var appended = await Alice.AppendPromptAsync(OnlyKey, prompt, 1, _lengthBefore).WaitAsync(Patience);
        Assert.True(appended.IsSuccessStatusCode, $"The append answered {(int)appended.StatusCode}: {await appended.Content.ReadAsStringAsync()}");
        Assert.False(_pass!.IsCompleted, "The pass finished before the append.");
    }

    [When("the test lets go of the session")]
    [When("the test lets go of the search entries")]
    public async Task WhenTheTestLetsGo()
    {
        await _hold!.CommitAsync();
        await _holder!.DisposeAsync();
        _holder = null;
    }

    [When("the test lets go of the session and the pass finishes")]
    public async Task WhenTheTestLetsGoAndThePassFinishes()
    {
        await WhenTheTestLetsGo();
        var result = await _pass!.WaitAsync(Patience);
        Assert.Equal(0, result.Failed);
    }

    [Then("listing alice's sessions shows both of them")]
    public async Task ThenListingShowsBoth() =>
        Assert.Equal(_keysByPrompt.Values.Order(), (await ListAsync()).Keys.Order());

    [Then("the held session is listed with its previous first prompt")]
    public async Task ThenTheHeldSessionIsListedWithItsPreviousFirstPrompt()
    {
        Assert.False(_pass!.IsCompleted, "The pass finished while the session was held.");
        Assert.Equal(StalePrompt, (await ListAsync())[_heldKey!]);
    }

    [Then("listing alice's sessions shows the first prompts {string} and {string}")]
    public async Task ThenListingShowsTheFirstPrompts(string first, string second)
    {
        var listed = await ListAsync();
        Assert.Equal(first, listed[_keysByPrompt[first]]);
        Assert.Equal(second, listed[_keysByPrompt[second]]);
    }

    [Then("both sessions are at the current parser version")]
    [Then("the session is at the current parser version")]
    public async Task ThenTheSessionsAreAtTheCurrentParserVersion()
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand("""SELECT "Summary_ParserVersion" FROM "Transcripts" """, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var versions = new List<int>();
        while (await reader.ReadAsync())
            versions.Add(reader.GetInt32(0));
        Assert.Equal(Enumerable.Repeat(TranscriptSummary.CurrentParserVersion, _keysByPrompt.Count), versions);
    }

    [Then("the append of {string} succeeds and the pass finishes")]
    public async Task ThenTheAppendSucceedsAndThePassFinishes(string prompt)
    {
        var appended = await _append!.WaitAsync(Patience);
        Assert.True(appended.IsSuccessStatusCode, $"The append answered {(int)appended.StatusCode}: {await appended.Content.ReadAsStringAsync()}");
        var result = await _pass!.WaitAsync(Patience);
        Assert.Equal(0, result.Failed);
        Assert.Equal(1, result.Summaries);
    }

    [Then("the pass finishes")]
    public async Task ThenThePassFinishes()
    {
        var result = await _pass!.WaitAsync(Patience);
        Assert.Equal(0, result.Failed);
        Assert.Equal(1, result.Summaries);
    }

    [Then("the session's search entries are at the current index version")]
    public async Task ThenTheSessionsSearchEntriesAreAtTheCurrentIndexVersion()
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(
            """SELECT count(DISTINCT "ChunkId"), count(*) FILTER (WHERE "IndexVersion" <> @version) FROM "SearchEntries" WHERE "TranscriptId" IS NOT NULL""",
            connection);
        command.Parameters.AddWithValue("version", SearchEntry.CurrentIndexVersion);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(2, reader.GetInt64(0));
        Assert.Equal(0, reader.GetInt64(1));
    }

    [Then("the session shows the first prompt {string}, {int} prompts and both chunks")]
    public async Task ThenTheSessionShows(string firstPrompt, int prompts)
    {
        var session = await GetSessionAsync(OnlyKey);
        Assert.Equal(firstPrompt, session["firstPrompt"]!.GetValue<string>());
        Assert.Equal(prompts, session["userMessages"]!.GetValue<int>());
        Assert.Equal(2, session["lines"]!.GetValue<int>());
        Assert.True(session["length"]!.GetValue<long>() > _lengthBefore, "The appended chunk is not stored.");
    }

    [Then("the registry logged no errors")]
    public void ThenTheRegistryLoggedNoErrors()
    {
        var errors = _scenario.Factory.Logs.Entries.Where(entry => entry.Level >= LogLevel.Error).ToList();
        Assert.True(errors.Count == 0, "The registry logged errors:\n" + string.Join("\n", errors));
    }

    public async ValueTask DisposeAsync()
    {
        if (_holder is not null)
            await _holder.DisposeAsync();
    }

    private async Task HoldAsync(string key, string lockingQuery)
    {
        _heldKey = key;
        _holder = await OpenAsync();
        _hold = await _holder.BeginTransactionAsync();
        await using var command = new NpgsqlCommand(lockingQuery, _holder, _hold);
        command.Parameters.AddWithValue("key", key);
        Assert.True((long)(await command.ExecuteScalarAsync())! > 0, "There was nothing to hold.");
    }

    // Waits until one more backend than before is blocked on a lock in this database. Fails fast if the work it waits
    // for finishes instead, which would mean it never needed the held row.
    private async Task WaitForAnotherWaiterAsync(Task work)
    {
        var expected = ++_waiting;
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM pg_stat_activity WHERE datname = @database AND wait_event_type = 'Lock'", connection);
        command.Parameters.AddWithValue("database", _scenario.Database);
        var deadline = DateTime.UtcNow + Patience;
        while ((long)(await command.ExecuteScalarAsync())! < expected)
        {
            if (work.IsCompleted)
            {
                await work;
                Assert.Fail("The work finished without waiting for the held session.");
            }
            Assert.True(DateTime.UtcNow < deadline, $"Expected {expected} backends waiting on the held session within {Patience}.");
            await Task.Delay(20);
        }
    }

    private async Task<Dictionary<string, string?>> ListAsync() =>
        (await Alice.GetJsonAsync("/v1/transcripts")).AsArray()
            .ToDictionary(t => t!["key"]!.GetValue<string>(), t => t!["firstPrompt"]?.GetValue<string>());

    private async Task<JsonNode> GetSessionAsync(string key) =>
        (await Alice.GetJsonAsync($"/v1/transcripts/{RegistryUser.Harness}/{key}"))["transcript"]!;

    private async Task<NpgsqlConnection> OpenAsync()
    {
        var connection = new NpgsqlConnection(RegistryInfrastructure.ConnectionString(_scenario.Database));
        await connection.OpenAsync();
        return connection;
    }
}
