using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Protostar.Registry.Api.Search;

namespace Protostar.Registry.Tests.Search.Domain;

/// <summary>Contract tests for <see cref="JsonLineRedactor"/>: secrets leave every string value, and every JSON line stays JSON.</summary>
/// <remarks>Fake secrets are assembled from pieces at run time, so no string in this file has a real secret's shape.</remarks>
public sealed class JsonLineRedactorTests
{
    private static readonly string GitHubToken = "gh" + "p_" + "abcdefghijklmnopqrstuvwxyz0123456789";
    private static readonly string AwsKey = "AK" + "IA" + "QX7RZ2MNB4QX7RZ2";
    private const string Password = "hunter2-supersecret";

    // Escapes quotes as \" the way harnesses written in JavaScript do; the default writes \u0022.
    private static readonly JsonSerializerOptions Relaxed = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static string Redact(string content) => Encoding.UTF8.GetString(PrefilterCheck.RedactJsonLines(Encoding.UTF8.GetBytes(content)));

    private static string RedactLine(string line) => Redact(line);

    private static void AssertNoSecrets(string text)
    {
        Assert.DoesNotContain(GitHubToken, text);
        Assert.DoesNotContain(AwsKey, text);
        Assert.DoesNotContain(Password, text);
    }

    [Fact]
    public void A_line_with_nothing_to_redact_comes_out_byte_for_byte()
    {
        // Odd spacing, escapes that need not be escapes, non-ASCII: none of it may be normalised.
        var line = """{ "type":"user",  "message": {"content": "caf\u00e9 \/ “quotes” ☃ <b>"}, "n": 1.50e3, "ok": true, "x": null }""";

        Assert.Equal(line, RedactLine(line));
    }

    [Fact]
    public void A_secret_in_a_string_value_is_replaced_and_the_rest_of_the_line_is_untouched()
    {
        var line = $$"""{"type":"user","message":{"role":"user","content":"my token is {{GitHubToken}} ok"},"n":1}""";

        var redacted = RedactLine(line);

        Assert.Equal("""{"type":"user","message":{"role":"user","content":"my token is [redacted:github-token] ok"},"n":1}""", redacted);
    }

    [Fact]
    public void A_secret_in_tool_input_nested_in_objects_and_arrays_is_replaced()
    {
        var line = new JsonObject
        {
            ["type"] = "assistant",
            ["message"] = new JsonObject
            {
                ["content"] = new JsonArray(new JsonObject
                {
                    ["type"] = "tool_use",
                    ["input"] = new JsonObject { ["command"] = $"export AWS_ACCESS_KEY_ID={AwsKey}", ["args"] = new JsonArray("--token", GitHubToken) },
                }),
            },
        }.ToJsonString();

        var redacted = RedactLine(line);

        AssertNoSecrets(redacted);
        var input = JsonNode.Parse(redacted)!["message"]!["content"]![0]!["input"]!;
        Assert.Equal("export AWS_ACCESS_KEY_ID=[redacted:aws-access-key]", input["command"]!.GetValue<string>());
        Assert.Equal("[redacted:github-token]", input["args"]![1]!.GetValue<string>());
    }

    [Fact]
    public void A_secret_inside_json_escaped_into_a_string_is_caught_and_both_levels_still_parse()
    {
        // A tool result whose text is itself JSON: on the line the inner quotes are written \".
        var inner = new JsonObject { ["password"] = Password, ["token"] = GitHubToken }.ToJsonString();
        var line = new JsonObject { ["type"] = "user", ["toolUseResult"] = new JsonObject { ["stdout"] = inner } }.ToJsonString(Relaxed);
        Assert.Contains("\\\"password\\\":\\\"", line);

        var redacted = RedactLine(line);

        AssertNoSecrets(redacted);
        var stdout = JsonNode.Parse(redacted)!["toolUseResult"]!["stdout"]!.GetValue<string>();
        var innerRedacted = JsonNode.Parse(stdout)!;
        Assert.Equal("[redacted:secret]", innerRedacted["password"]!.GetValue<string>());
        Assert.Equal("[redacted:github-token]", innerRedacted["token"]!.GetValue<string>());
    }

    [Fact]
    public void A_secret_spelled_with_unicode_escapes_is_caught()
    {
        // \u0067 is 'g': the line never holds the token's literal bytes, only its JSON spelling.
        var line = $$"""{"content":"\u0067{{GitHubToken[1..]}}"}""";

        var redacted = RedactLine(line);

        Assert.Equal("""{"content":"[redacted:github-token]"}""", redacted);
    }

    [Fact]
    public void A_redacted_string_keeps_the_characters_around_the_secret_correctly_escaped()
    {
        var text = $"line one\n\"quoted\" \\ tab\t café ☃ {GitHubToken}";
        var line = new JsonObject { ["content"] = text }.ToJsonString();

        var redacted = RedactLine(line);

        Assert.Equal(text.Replace(GitHubToken, "[redacted:github-token]"), JsonNode.Parse(redacted)!["content"]!.GetValue<string>());
    }

    [Fact]
    public void A_value_whose_only_sign_of_a_secret_is_its_property_name_is_replaced()
    {
        var line = $$"""{"password":"{{Password}}","db_password": "{{Password}}","note":"{{Password}}"}""";

        var redacted = RedactLine(line);

        // Judged with its name, as password=... is in text; the same value under a harmless name is not a secret.
        Assert.Equal($$"""{"password":"[redacted:secret]","db_password": "[redacted:secret]","note":"{{Password}}"}""", redacted);
    }

    // registry#78, the issue's repros: JSON in a Bash command and in code, nested inside another string.
    [Fact]
    public void A_password_in_a_curl_body_inside_a_bash_command_is_replaced()
    {
        var line = """{"type":"assistant","message":{"content":[{"type":"tool_use","name":"Bash","input":{"command":"curl -X POST -d \"{\\\"password\\\":\\\"hunter2-supersecret\\\"}\" https://x"}}]}}""";

        var redacted = RedactLine(line);

        AssertNoSecrets(redacted);
        var command = JsonNode.Parse(redacted)!["message"]!["content"]![0]!["input"]!["command"]!.GetValue<string>();
        Assert.Equal("curl -X POST -d \"{\\\"password\\\":\\\"[redacted:secret]\\\"}\" https://x", command);
    }

    [Fact]
    public void An_api_key_in_a_json_literal_in_code_is_replaced()
    {
        var line = """{"new_string":"var body = \"{\\\"api_key\\\": \\\"abcd1234efgh5678\\\"}\";"}""";

        var redacted = RedactLine(line);

        Assert.Equal("""{"new_string":"var body = \"{\\\"api_key\\\": \\\"[redacted:secret]\\\"}\";"}""", redacted);
    }

    [Fact]
    public void An_escaped_password_on_a_line_that_is_not_json_is_replaced() =>
        Assert.Equal("not json \\\"password\\\": \\\"[redacted:secret]\\\"", RedactLine("not json \\\"password\\\": \\\"hunter2-supersecret\\\""));

    [Fact]
    public void A_password_behind_two_levels_of_json_in_a_string_is_replaced()
    {
        var inner = new JsonObject { ["password"] = Password }.ToJsonString();
        var middle = new JsonObject { ["body"] = $"curl -d '{inner}'" }.ToJsonString(Relaxed);
        var line = new JsonObject { ["content"] = $"ran: {middle}" }.ToJsonString(Relaxed);

        var redacted = RedactLine(line);

        AssertNoSecrets(redacted);
        Assert.Contains("[redacted:secret]", redacted);
        JsonDocument.Parse(redacted).Dispose();
    }

    // registry#79, the issue's repros: a value under a secret name is replaced whole.
    [Theory]
    [InlineData("""{"password":"correct horse battery staple"}""", """{"password":"[redacted:secret]"}""")]
    [InlineData("""{"password":"p4ss;w0rd-tail"}""", """{"password":"[redacted:secret]"}""")]
    [InlineData("""{"api_key":"it's, \"quoted\"; long"}""", """{"api_key":"[redacted:secret]"}""")]
    [InlineData("""{"password":" hunter2-supersecret"}""", """{"password":"[redacted:secret]"}""")]
    [InlineData("""{"password":"abc"}""", """{"password":"abc"}""")]
    [InlineData("""{"max_tokens":"4096 tokens"}""", """{"max_tokens":"4096 tokens"}""")]
    public void A_value_under_a_secret_name_is_replaced_whole(string line, string expected) =>
        Assert.Equal(expected, RedactLine(line));

    [Fact]
    public void A_value_under_a_secret_name_keeps_a_more_specific_type()
    {
        var awsSecret = "wJalrXUtnFEMI/K7MDENG+bPxRfiCY" + "EXAMPLEKEY";
        var line = $$"""{"aws_secret_access_key":"{{awsSecret}}","token":"{{GitHubToken}}"}""";

        Assert.Equal("""{"aws_secret_access_key":"[redacted:aws-secret-key]","token":"[redacted:github-token]"}""", RedactLine(line));
    }

    [Fact]
    public void The_value_of_a_name_value_pair_naming_a_secret_is_replaced()
    {
        var line = """{"env":[{"name":"DB_PASSWORD","value":"s3cr3t value"},{"name":"LOG_LEVEL","value":"debug info"},{"key":"API_TOKEN","value":"abcd1234efgh"}]}""";

        Assert.Equal(
            """{"env":[{"name":"DB_PASSWORD","value":"[redacted:secret]"},{"name":"LOG_LEVEL","value":"debug info"},{"key":"API_TOKEN","value":"[redacted:secret]"}]}""",
            RedactLine(line));
    }

    [Fact]
    public void Property_names_are_left_alone()
    {
        var line = $$"""{"{{GitHubToken}}":1}""";

        Assert.Equal(line, RedactLine(line));
    }

    [Fact]
    public void A_line_that_is_not_json_is_redacted_as_plain_text()
    {
        Assert.Equal("not json: token [redacted:github-token]", RedactLine($"not json: token {GitHubToken}"));
        Assert.Equal("""{"cut off":"[redacted:github-token]""", RedactLine($$"""{"cut off":"{{GitHubToken}}"""));
    }

    [Fact]
    public void Lines_are_redacted_one_by_one_and_their_endings_are_kept()
    {
        var content = $"{{\"a\":\"{GitHubToken}\"}}\r\n\n{{\"b\":\"plain\"}}\n{{\"c\":\"{AwsKey}\"}}";

        var redacted = Redact(content);

        Assert.Equal("{\"a\":\"[redacted:github-token]\"}\r\n\n{\"b\":\"plain\"}\n{\"c\":\"[redacted:aws-access-key]\"}", redacted);
    }

    [Fact]
    public void Redacting_twice_changes_nothing_more()
    {
        var line = new JsonObject { ["content"] = $"password={Password} and {GitHubToken}" }.ToJsonString();

        var once = RedactLine(line);

        Assert.Equal(once, RedactLine(once));
    }

    [Fact]
    public void Random_shapes_holding_secrets_stay_valid_json_and_lose_every_secret()
    {
        var random = new Random(18);
        string[] secrets =
        [
            GitHubToken, AwsKey, $"password={Password}", $"\"password\": \"{Password}\"", $"password: \"correct horse {Password}\"",
            $"curl -d \"{{\\\"password\\\":\\\"{Password}\\\"}}\"", $"{{\"name\": \"DB_PASSWORD\", \"value\": \"a b {Password}\"}}",
        ];
        string[] filler = ["plain words", "café ☃", "a \"quote\"", "back\\slash", "new\nline", "<html>&amp;", ""];

        string Text() => string.Join(' ', Enumerable.Range(0, random.Next(1, 4))
            .Select(_ => random.Next(3) == 0 ? secrets[random.Next(secrets.Length)] : filler[random.Next(filler.Length)]));

        JsonNode Node(int depth) => (depth > 3 ? random.Next(3) : random.Next(6)) switch
        {
            0 => JsonValue.Create(Text()),
            1 => JsonValue.Create(random.Next(1000)),
            2 => JsonValue.Create(new JsonObject { ["password"] = Text(), ["n"] = 1 }.ToJsonString()), // JSON escaped into a string
            3 => new JsonArray(Enumerable.Range(0, random.Next(1, 4)).Select(_ => (JsonNode?)Node(depth + 1)).ToArray()),
            _ => new JsonObject(Enumerable.Range(0, random.Next(1, 4)).Select(i => KeyValuePair.Create($"k{i}", (JsonNode?)Node(depth + 1)))),
        };

        for (var i = 0; i < 500; i++)
        {
            var line = new JsonObject { ["type"] = "user", ["body"] = Node(0) }.ToJsonString();

            var redacted = RedactLine(line);

            using var parsed = JsonDocument.Parse(redacted);
            AssertNoSecrets(redacted);
            AssertNoSecrets(Unescaped(parsed.RootElement));
        }
    }

    // Every string value in the document, decoded, so a secret hiding behind JSON escapes would show.
    private static string Unescaped(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString()!,
        JsonValueKind.Array => string.Join('\n', element.EnumerateArray().Select(Unescaped)),
        JsonValueKind.Object => string.Join('\n', element.EnumerateObject().Select(p => Unescaped(p.Value))),
        _ => "",
    };
}
