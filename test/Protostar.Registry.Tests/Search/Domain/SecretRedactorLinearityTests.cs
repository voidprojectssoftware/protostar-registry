using System.Buffers;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Protostar.Registry.Api.Search;

namespace Protostar.Registry.Tests.Search.Domain;

/// <summary>Contract tests for how long <see cref="SecretRedactor"/> may take, and what it does when a pattern runs out of time.</summary>
/// <remarks>
/// Redaction runs inline in chunk appends and redacted reads, so a pattern that backtracks quadratically lets anyone who
/// can upload text tie up the server's CPU (registry#80: a 400 KB base64url blob took 160 s). Each input here is a
/// megabyte shaped to make one pattern start and fail an attempt at every position it can. Linear patterns take a
/// fraction of a second on them; the bound is generous so a slow CI machine does not flake, and a quadratic pattern
/// would blow it by orders of magnitude.
/// </remarks>
public sealed class SecretRedactorLinearityTests
{
    private const int Megabyte = 1_000_000;
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(4);

    private static string Fill(string unit, int length)
    {
        var text = new StringBuilder(length + unit.Length);
        while (text.Length < length)
            text.Append(unit);
        return text.ToString(0, length);
    }

    private static string Base64Url(int length)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";
        var random = new Random(80);
        return string.Create(length, random, (span, r) =>
        {
            for (var i = 0; i < span.Length; i++)
                span[i] = alphabet[r.Next(alphabet.Length)];
        });
    }

    private static string Dotted(int length)
    {
        var text = new StringBuilder(length + 16);
        for (var i = 0; text.Length < length; i++)
            text.Append('a').Append(i).Append('.');
        return text.ToString(0, length);
    }

    public static TheoryData<string> Shapes() =>
    [
        "base64url", "dotted", "dashed", "token-colons", "token-equals", "open-quotes", "escaped-open-quotes",
        "jwt-starts", "openai-starts", "pem-headers", "url-schemes", "connection-strings", "bearers", "name-value-pairs",
        "github-starts", "spaces",
    ];

    // Each shape ends with, or is made of, the words that switch the slow patterns' prefilters on.
    private static string Adversarial(string shape) => shape switch
    {
        "base64url" => Base64Url(Megabyte) + " token",
        "dotted" => Dotted(Megabyte) + " token",
        "dashed" => Fill("a-", Megabyte) + " token",
        "token-colons" => Fill("token:", Megabyte),
        "token-equals" => Fill("token=", Megabyte),
        "open-quotes" => Fill("password: \"", Megabyte),
        "escaped-open-quotes" => Fill("\\\"password\\\":\\\"x ", Megabyte),
        "jwt-starts" => Fill("eyJ-", Megabyte),
        "openai-starts" => Fill("sk-", Megabyte),
        "pem-headers" => Fill("-----BEGIN " + "RSA PRIVATE KEY-----", Megabyte),
        "url-schemes" => Dotted(Megabyte) + "://u:p",
        "connection-strings" => Fill("password=a;", Megabyte),
        "bearers" => Fill("Bearer ", Megabyte / 2) + Fill("a-", Megabyte / 2),
        "name-value-pairs" => Fill("\"name\":\"DB_PASSWORD\",\"value\":\"", Megabyte),
        "github-starts" => Fill("gh" + "p_", Megabyte),
        "spaces" => "token" + Fill(" ", Megabyte) + "x",
        _ => throw new ArgumentOutOfRangeException(nameof(shape)),
    };

    [Theory]
    [MemberData(nameof(Shapes))]
    public void A_megabyte_shaped_to_backtrack_is_redacted_in_linear_time(string shape)
    {
        var text = Adversarial(shape);

        // Every pattern runs, whatever the prefilters would skip.
        var watch = Stopwatch.StartNew();
        string redacted;
        using (SecretRedactor.WithoutPrefilters())
            redacted = SecretRedactor.Redact(text);
        watch.Stop();

        Assert.NotEqual(SecretRedactor.Unscannable, redacted);
        Assert.True(watch.Elapsed < Bound, $"Redacting the {shape} megabyte took {watch.ElapsedMilliseconds} ms.");
    }

    [Fact]
    public void A_gmail_style_response_line_is_redacted_in_linear_time()
    {
        // The issue's sample: a tool result holding a Gmail API response, a megabyte of base64url beside a nextPageToken.
        var line = "{\"type\":\"user\",\"message\":{\"content\":[{\"type\":\"tool_result\",\"content\":\"{\\\"messages\\\":[{\\\"payload\\\":{\\\"body\\\":{\\\"data\\\":\\\""
            + Base64Url(Megabyte) + "\\\"}}}],\\\"nextPageToken\\\":\\\"0123456789\\\"}\"}]}}";
        var output = new ArrayBufferWriter<byte>();

        var watch = Stopwatch.StartNew();
        JsonLineRedactor.Redact(Encoding.UTF8.GetBytes(line), output);
        watch.Stop();

        Assert.DoesNotContain("unscannable", Encoding.UTF8.GetString(output.WrittenSpan));
        Assert.True(watch.Elapsed < Bound, $"Redacting the line took {watch.ElapsedMilliseconds} ms.");
    }

    [Fact]
    public void Every_pattern_has_the_match_timeout()
    {
        foreach (var (type, regex) in SecretRedactor.Regexes)
            Assert.True(regex.MatchTimeout == SecretRedactor.MatchTimeout, $"The pattern {type} has a match timeout of {regex.MatchTimeout}.");
        Assert.True(SecretRedactor.MatchTimeout > TimeSpan.Zero && SecretRedactor.MatchTimeout != Regex.InfiniteMatchTimeout);
    }

    // A pattern that backtracks exponentially on a run of a's that does not end the text, with a tiny timeout.
    private static readonly Regex Catastrophic = new("^(a+)+$", RegexOptions.None, TimeSpan.FromMilliseconds(20));

    private static readonly string Unscannable = new string('a', 40) + "! with a password=hunter2-supersecret";

    [Fact]
    public void Text_a_pattern_cannot_finish_scanning_comes_back_as_one_marker()
    {
        using var added = SecretRedactor.WithAddedPattern(Catastrophic);

        Assert.Equal("[redacted:unscannable]", SecretRedactor.Redact(Unscannable));
        Assert.Equal("password=[redacted:secret]", SecretRedactor.Redact("password=hunter2-supersecret"));
    }

    [Fact]
    public void A_json_string_that_cannot_be_scanned_becomes_the_marker_and_the_line_stays_json()
    {
        var line = System.Text.Json.JsonSerializer.Serialize(new { slow = Unscannable, fine = "plain words" });
        var output = new ArrayBufferWriter<byte>();

        using (SecretRedactor.WithAddedPattern(Catastrophic))
            JsonLineRedactor.Redact(Encoding.UTF8.GetBytes(line + "\n" + Unscannable), output);

        var lines = Encoding.UTF8.GetString(output.WrittenSpan).Split('\n');
        Assert.Equal("""{"slow":"[redacted:unscannable]","fine":"plain words"}""", lines[0]);
        Assert.Equal("[redacted:unscannable]", lines[1]);
    }
}
