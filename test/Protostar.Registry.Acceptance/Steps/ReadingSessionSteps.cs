using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Protostar.Registry.Acceptance.Support;
using Reqnroll;
using Xunit;

namespace Protostar.Registry.Acceptance.Steps;

/// <summary>Steps for ReadingSessions.feature: a redacted read hides secrets and stays JSON, the default read is the upload.</summary>
[Binding]
public sealed class ReadingSessionSteps
{
    // Well-formed fakes, assembled here so the literals are not in the repository.
    private static readonly string GitHubToken = "gh" + "p_" + "abcdefghijklmnopqrstuvwxyz0123456789";
    private static readonly string AwsKey = "AK" + "IA" + "QX7RZ2MNB4QX7RZ2";
    private const string Password = "hunter2-supersecret";

    // Escapes quotes as \" the way Claude Code (JavaScript) writes them.
    private static readonly JsonSerializerOptions AsHarnessWrites = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly RegistryScenario _scenario;
    private string? _key;
    private byte[]? _uploaded;
    private string? _plainLine;
    private byte[]? _read;

    public ReadingSessionSteps(RegistryScenario scenario) => _scenario = scenario;

    private string Read => Encoding.UTF8.GetString(_read ?? throw new InvalidOperationException("Nothing has been read in this scenario."));

    [Given("{word} has backed up a session with a GitHub token in her prompt, an AWS key in a tool's input, and a password in a tool's JSON output")]
    public async Task GivenHasBackedUpASessionWithSecrets(string login)
    {
        var user = _scenario.User(login);
        _key = await user.BackUpSessionAsync(key =>
        {
            var toolUse = new JsonObject
            {
                ["type"] = "assistant",
                ["sessionId"] = key,
                ["message"] = new JsonObject
                {
                    ["role"] = "assistant",
                    ["content"] = new JsonArray(new JsonObject
                    {
                        ["type"] = "tool_use",
                        ["name"] = "Bash",
                        ["input"] = new JsonObject { ["command"] = $"aws configure set aws_access_key_id {AwsKey}" },
                    }),
                },
            };

            // The tool printed JSON, so on the line its quotes are escaped: \"password\": \"...\".
            var printed = new JsonObject { ["user"] = "app", ["password"] = Password }.ToJsonString(AsHarnessWrites);
            var toolResult = new JsonObject
            {
                ["type"] = "user",
                ["sessionId"] = key,
                ["message"] = new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = new JsonArray(new JsonObject { ["type"] = "tool_result", ["content"] = printed }),
                },
            };

            // Spacing and non-ASCII a serializer would normalise, to show an untouched line keeps its exact bytes.
            _plainLine = $$$"""{"type": "assistant", "sessionId": "{{{key}}}", "message": {"role": "assistant", "content": "Done: caf\u00e9 ☃ “ok”"}}""";

            _uploaded = Encoding.UTF8.GetBytes(string.Join("\n",
                user.PromptLine(key, $"deploy it with my token {GitHubToken} please").ToJsonString(),
                toolUse.ToJsonString(AsHarnessWrites),
                toolResult.ToJsonString(AsHarnessWrites),
                _plainLine) + "\n");
            return _uploaded;
        });
    }

    [When("{word} reads the session redacted")]
    public Task WhenReadsTheSessionRedacted(string login) => DownloadAsync(login, "?redacted=true");

    [When("{word} downloads the session")]
    public Task WhenDownloadsTheSession(string login) => DownloadAsync(login, "");

    [Then("every line of what she reads is valid JSON")]
    public void ThenEveryLineIsValidJson()
    {
        var lines = Read.Split('\n');
        Assert.Equal("", lines[^1]);
        Assert.Equal(4, lines.Length - 1);
        foreach (var line in lines[..^1])
            JsonDocument.Parse(line).Dispose();
    }

    [Then("what she reads shows {string}, {string} and {string}")]
    public void ThenWhatSheReadsShows(string first, string second, string third)
    {
        Assert.Contains(first, Read);
        Assert.Contains(second, Read);
        Assert.Contains(third, Read);
    }

    [Then("what she reads contains none of the secrets")]
    public void ThenWhatSheReadsContainsNoneOfTheSecrets()
    {
        foreach (var secret in new[] { GitHubToken, AwsKey, Password })
            Assert.DoesNotContain(secret, Read);
    }

    [Then("the line without a secret reads exactly as uploaded")]
    public void ThenTheLineWithoutASecretReadsExactlyAsUploaded() => Assert.Equal(_plainLine, Read.Split('\n')[3]);

    [Then("what she reads is byte-identical to what she uploaded")]
    public void ThenWhatSheReadsIsByteIdenticalToWhatSheUploaded() => Assert.Equal(_uploaded, _read);

    private async Task DownloadAsync(string login, string query)
    {
        var response = await _scenario.User(login).Http.GetAsync($"/v1/transcripts/{RegistryUser.Harness}/{_key}/content{query}");
        Assert.True(response.IsSuccessStatusCode, $"Reading the session answered {(int)response.StatusCode}.");
        Assert.Equal("application/x-ndjson", response.Content.Headers.ContentType?.MediaType);
        _read = await response.Content.ReadAsByteArrayAsync();
    }
}
