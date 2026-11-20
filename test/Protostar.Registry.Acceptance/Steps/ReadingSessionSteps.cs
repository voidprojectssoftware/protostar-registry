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

    // The values of registry#78, #79 and the shapes beside them, each of which a redacted read once served.
    private const string CurlPassword = "curl-body-pa55word";
    private const string CodeApiKey = "abcd1234efgh5678";
    private const string Passphrase = "correct horse battery staple";
    private const string SemicolonPassword = "p4ss;w0rd-tail";
    private const string KubernetesSecret = "kube s3cret value";
    private static readonly string BearerToken = "ya29." + "a0B1c2D3e4F5g6H7i8J9k0L1m2N3o4P5";

    private readonly RegistryScenario _scenario;
    private readonly List<string> _secrets = [GitHubToken, AwsKey, Password];
    private string? _key;
    private byte[]? _uploaded;
    private string? _plainLine;
    private byte[]? _read;
    private int _lineCount;

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

            _lineCount = 4;
            _uploaded = Encoding.UTF8.GetBytes(string.Join("\n",
                user.PromptLine(key, $"deploy it with my token {GitHubToken} please").ToJsonString(),
                toolUse.ToJsonString(AsHarnessWrites),
                toolResult.ToJsonString(AsHarnessWrites),
                _plainLine) + "\n");
            return _uploaded;
        });
    }

    [Given("{word} has backed up a session with a password in a curl body, an API key in code, a passphrase, a password with a semicolon, a Kubernetes secret, a bearer token and a GitHub token followed by an underscore")]
    public async Task GivenHasBackedUpASessionWithNestedAndWholeValueSecrets(string login)
    {
        var user = _scenario.User(login);
        _secrets.Clear();
        _secrets.AddRange([CurlPassword, CodeApiKey, Passphrase, "w0rd-tail", KubernetesSecret, BearerToken, GitHubToken]);
        _key = await user.BackUpSessionAsync(key =>
        {
            JsonObject ToolUse(string name, JsonObject input) => new()
            {
                ["type"] = "assistant",
                ["sessionId"] = key,
                ["message"] = new JsonObject
                {
                    ["role"] = "assistant",
                    ["content"] = new JsonArray(new JsonObject { ["type"] = "tool_use", ["name"] = name, ["input"] = input }),
                },
            };

            // JSON in a shell string in a command: decoded, the command still reads \"password\":\"...\".
            var curlBody = new JsonObject { ["password"] = CurlPassword }.ToJsonString().Replace("\"", "\\\"");
            var curl = ToolUse("Bash", new JsonObject { ["command"] = $"curl -X POST -d \"{curlBody}\" https://api.example.com/login" });
            var code = ToolUse("Edit", new JsonObject { ["new_string"] = $"var body = \"{{\\\"api_key\\\": \\\"{CodeApiKey}\\\"}}\";" });
            var toolResult = new JsonObject
            {
                ["type"] = "user",
                ["sessionId"] = key,
                ["toolUseResult"] = new JsonObject
                {
                    ["password"] = Passphrase,
                    ["db_password"] = SemicolonPassword,
                    ["env"] = new JsonArray(new JsonObject { ["name"] = "DB_PASSWORD", ["value"] = KubernetesSecret }),
                },
            };
            var prompt = user.PromptLine(key, $"call it with Authorization: Bearer {BearerToken} and push with {GitHubToken}_backup");

            _uploaded = Encoding.UTF8.GetBytes(string.Join("\n",
                curl.ToJsonString(AsHarnessWrites), code.ToJsonString(AsHarnessWrites), toolResult.ToJsonString(AsHarnessWrites), prompt.ToJsonString()) + "\n");
            _lineCount = 4;
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
        Assert.Equal(_lineCount, lines.Length - 1);
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
        foreach (var secret in _secrets)
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
