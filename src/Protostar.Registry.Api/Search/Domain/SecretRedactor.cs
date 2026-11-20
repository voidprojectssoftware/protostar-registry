using System.Buffers;
using System.Text.RegularExpressions;

namespace Protostar.Registry.Api.Search;

/// <summary>Replaces known secret patterns in text with a typed marker such as <c>[redacted:github-token]</c>.</summary>
/// <remarks>
/// <para>
/// The registry keeps what clients upload verbatim (transcript chunks, memory versions), because reparsing and
/// pulling need the original bytes, and those are already readable only by their owner. What the registry derives
/// for people and agents to read (the search index, its snippets, the titles and first prompts listings show)
/// goes through <see cref="Redact"/> first (registry#18). A transcript read with <c>redacted=true</c> goes through it
/// too, line by line, by way of <see cref="JsonLineRedactor"/>.
/// </para>
/// <para>
/// The pattern set is curated, not exhaustive: each pattern matches a format with a recognisable shape (a fixed
/// prefix, a fixed length, a PEM header) or a value assigned to a name that says it is secret. Plain hex (commit
/// hashes), GUIDs and base64 blobs such as inline images have no such shape and are left alone. Patterns run in
/// order, most specific first, and the catch-all assignment pattern skips values that are already markers, so a
/// <c>token=ghp_...</c> keeps its <c>github-token</c> type.
/// </para>
/// <para>
/// Changing the patterns changes what the index holds, so bump <see cref="SearchEntry.CurrentIndexVersion"/> with
/// any change here; the backfill then re-indexes everything.
/// </para>
/// </remarks>
public static partial class SecretRedactor
{
    // Built once: Redact runs on every string value of a transcript read with redacted=true, so per-call setup counts.
    private static readonly Pattern[] All = Patterns().ToArray();

    /// <summary>The type names a marker can carry, in the order the patterns run.</summary>
    public static IReadOnlyList<string> Types { get; } = All.Select(p => p.Type).ToList();

    /// <summary>Returns <paramref name="text"/> with every known secret replaced by <c>[redacted:&lt;type&gt;]</c>.</summary>
    /// <remarks>Text with no secrets comes back unchanged (the same instance). Redacting already redacted text changes nothing.</remarks>
    public static string Redact(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        foreach (var pattern in All)
        {
            if (!_prefiltersOff && pattern.Prefilter.Skips(text))
                continue;

            text = pattern.Group is null
                ? pattern.Regex.Replace(text, pattern.Marker)
                : pattern.Regex.Replace(text, pattern.Evaluator);
        }

        return text;
    }

    // The match with only its secret group replaced.
    private static string KeepAround(Match match, Group secret, string marker)
    {
        var start = secret.Index - match.Index;
        return string.Concat(match.Value.AsSpan(0, start), marker, match.Value.AsSpan(start + secret.Length));
    }

    /// <summary>Redacts <paramref name="text"/>, passing <c>null</c> through.</summary>
    public static string? RedactOrNull(string? text) => text is null ? null : Redact(text);

    /// <summary>The marker that replaces a secret of <paramref name="type"/>.</summary>
    public static string Marker(string type) => $"[redacted:{type}]";

    // A pattern, and the marker that replaces what it matches. With a group, only that group is replaced and the rest
    // of the match (a name, an equals sign, a URL's user) stays readable.
    private sealed record Pattern(string Type, Regex Regex, Prefilter Prefilter, string? Group = null)
    {
        public string Marker { get; } = SecretRedactor.Marker(Type);

        public MatchEvaluator Evaluator { get; } = m => KeepAround(m, m.Groups[Group!], SecretRedactor.Marker(Type));
    }

    /// <summary>Whether a pattern may be skipped for text that holds none of a set of literals.</summary>
    /// <remarks>
    /// Every pattern declares one. <see cref="None"/> is for patterns with a literal prefix of their own, which the
    /// regex engine already searches for at several GB/s. The rest (about 50 to 110 MB/s, because nothing fixed starts
    /// a match) list literals of which every match holds at least one. A list that misses one would skip a secret, so
    /// the unit tests run every sample with and without prefilters and require the same output.
    /// </remarks>
    internal sealed class Prefilter
    {
        private readonly SearchValues<string>? _values;

        private Prefilter(bool applies, StringComparison comparison, string[] literals)
        {
            Applies = applies;
            Comparison = comparison;
            Literals = literals;
            // An empty list would skip every text; leave it inert here, and let the declaration test name it.
            _values = applies && literals.Length > 0 ? SearchValues.Create(literals, comparison) : null;
        }

        /// <summary>The pattern always runs.</summary>
        public static Prefilter None { get; } = new(false, StringComparison.Ordinal, []);

        /// <summary>The pattern runs only on text holding at least one of <paramref name="literals"/>.</summary>
        public static Prefilter AnyOf(StringComparison comparison, params string[] literals) => new(true, comparison, literals);

        public bool Applies { get; }
        public StringComparison Comparison { get; }
        public IReadOnlyList<string> Literals { get; }

        /// <summary>True when the pattern cannot match <paramref name="text"/> and need not run.</summary>
        public bool Skips(string text) => _values is not null && text.AsSpan().IndexOfAny(_values) < 0;
    }

    /// <summary>Each pattern's type and the prefilter it declares, in the order the patterns run; for tests.</summary>
    internal static IReadOnlyList<(string Type, Prefilter? Prefilter)> Prefilters { get; } = All.Select(p => (p.Type, (Prefilter?)p.Prefilter)).ToList();

    [ThreadStatic]
    private static bool _prefiltersOff;

    /// <summary>Runs every pattern on every text, on this thread, until the result is disposed; for tests.</summary>
    /// <remarks>Lets a test compare the prefiltered output with the full one. Synchronous callers only: the switch is per thread.</remarks>
    internal static IDisposable WithoutPrefilters()
    {
        _prefiltersOff = true;
        return new PrefiltersBack();
    }

    private sealed class PrefiltersBack : IDisposable
    {
        public void Dispose() => _prefiltersOff = false;
    }

    // Order matters; see the class remarks.
    private static IEnumerable<Pattern> Patterns()
    {
        var none = Prefilter.None;
        yield return new("private-key", PrivateKey(), none);
        yield return new("jwt", Jwt(), none);
        yield return new("url-password", UrlPassword(), Prefilter.AnyOf(StringComparison.Ordinal, "://"), "secret");
        yield return new("connection-string-password", ConnectionStringPassword(), Prefilter.AnyOf(StringComparison.OrdinalIgnoreCase, "password", "pwd"), "secret");
        yield return new("azure-storage-key", AzureStorageKey(), none, "secret");
        yield return new("azure-sas-signature", AzureSasSignature(), none, "secret");
        yield return new("azure-client-secret", AzureClientSecret(), Prefilter.AnyOf(StringComparison.Ordinal, "Q~"));
        yield return new("aws-access-key", AwsAccessKey(), none);
        yield return new("aws-secret-key", AwsSecretKey(), none, "secret");
        yield return new("github-token", GitHubToken(), none);
        yield return new("anthropic-key", AnthropicKey(), none);
        yield return new("openai-key", OpenAiKey(), none);
        yield return new("google-api-key", GoogleApiKey(), none);
        yield return new("slack-token", SlackToken(), none);
        yield return new("stripe-key", StripeKey(), none);
        // The stems stop before "key": under (?i) the pattern's k also matches the Kelvin sign (U+212A), which
        // OrdinalIgnoreCase does not equate with k.
        yield return new("secret", SecretAssignment(), Prefilter.AnyOf(StringComparison.OrdinalIgnoreCase,
            "password", "passwd", "secret", "token", "api", "access", "private"), "secret");
    }

    private const RegexOptions Options = RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture;

    // A PEM private key block of any kind (RSA, EC, OPENSSH, ENCRYPTED, PKCS#8). A block cut off before its END line
    // (a truncated paste) is redacted through the end of its base64 body, including JSON-escaped "\n" separators.
    [GeneratedRegex(@"-----BEGIN [A-Z0-9 ]*PRIVATE KEY-----(?:[\s\S]*?-----END [A-Z0-9 ]*PRIVATE KEY-----|(?:[A-Za-z0-9+/=\r\n]|\\[nr])*)", Options)]
    private static partial Regex PrivateKey();

    // Three base64url segments, the first two JSON objects ("eyJ" is base64 for '{"').
    [GeneratedRegex(@"\beyJ[A-Za-z0-9_\-]{8,}\.eyJ[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]{8,}", Options)]
    private static partial Regex Jwt();

    // scheme://user:password@host: only the password.
    [GeneratedRegex(@"\b[A-Za-z][A-Za-z0-9+.\-]*://[^\s/:@""']+:(?<secret>[^\s@/""']+)@", Options)]
    private static partial Regex UrlPassword();

    // ADO.NET, ODBC and libpq keyword connection strings: Password=...; Pwd=... A semicolon before the keyword or after
    // the value is what makes it a connection string; a lone password=... is left to the assignment pattern.
    [GeneratedRegex(@"(?i)(?:(?<=;\s*)(?:password|pwd)\s*=\s*(?!\[redacted:)(?<secret>[^;\s""']+)|(?<![A-Za-z0-9_])(?:password|pwd)\s*=\s*(?!\[redacted:)(?<secret>[^;\s""']+)(?=\s*;))", Options)]
    private static partial Regex ConnectionStringPassword();

    // Azure Storage connection strings: AccountKey=<base64>.
    [GeneratedRegex(@"(?i)\bAccountKey\s*=\s*(?<secret>[A-Za-z0-9+/]{20,}={0,2})", Options)]
    private static partial Regex AzureStorageKey();

    // Azure SAS tokens: the sig= parameter of a signed URL or query.
    [GeneratedRegex(@"[?&]sig=(?<secret>[A-Za-z0-9%+/=]{16,})", Options)]
    private static partial Regex AzureSasSignature();

    // Microsoft Entra ID (Azure AD) client secrets: three characters, a digit, "Q~", then 31 to 34 more.
    [GeneratedRegex(@"(?<![A-Za-z0-9_~.\-])[A-Za-z0-9_~.\-]{3}\dQ~[A-Za-z0-9_~.\-]{31,34}(?![A-Za-z0-9_~.\-])", Options)]
    private static partial Regex AzureClientSecret();

    // AWS access key ids: a four-letter prefix naming the key kind, then 16 uppercase letters or digits.
    [GeneratedRegex(@"\b(?:AKIA|ASIA|ABIA|ACCA|AGPA|AIDA|AROA|ANPA|ANVA|AIPA)[A-Z0-9]{16}\b", Options)]
    private static partial Regex AwsAccessKey();

    // AWS secret access keys have no prefix, only a length (40), so only one assigned to a name that says what it is.
    [GeneratedRegex(@"(?i)\baws_?secret(?:_?access)?_?key[""']?\s*[:=]\s*[""']?(?<secret>[A-Za-z0-9/+]{40})(?![A-Za-z0-9/+])", Options)]
    private static partial Regex AwsSecretKey();

    // GitHub tokens: classic personal (ghp_), OAuth (gho_), user-to-server (ghu_), server-to-server (ghs_), refresh
    // (ghr_), and fine-grained personal tokens (github_pat_).
    [GeneratedRegex(@"\b(?:gh[pousr]_[A-Za-z0-9]{36,255}|github_pat_[A-Za-z0-9_]{22,255})\b", Options)]
    private static partial Regex GitHubToken();

    [GeneratedRegex(@"\bsk-ant-[A-Za-z0-9_\-]{20,}", Options)]
    private static partial Regex AnthropicKey();

    // OpenAI keys: sk-, sk-proj-, sk-svcacct-, sk-admin-. A digit is required so a long kebab-case name starting
    // "sk-" is not taken for a key.
    [GeneratedRegex(@"\bsk-(?=[A-Za-z0-9_\-]*\d)[A-Za-z0-9_\-]{20,}", Options)]
    private static partial Regex OpenAiKey();

    // Google API keys (Maps, Gemini, Firebase and other GCP APIs).
    [GeneratedRegex(@"\bAIza[0-9A-Za-z_\-]{35}(?![0-9A-Za-z_\-])", Options)]
    private static partial Regex GoogleApiKey();

    [GeneratedRegex(@"\bxox[abposr]-[A-Za-z0-9\-]{10,}", Options)]
    private static partial Regex SlackToken();

    [GeneratedRegex(@"\b(?:sk|rk)_live_[A-Za-z0-9]{16,}", Options)]
    private static partial Regex StripeKey();

    // A value assigned to a name that says it is secret: password=..., "client_secret": "...", API_KEY: ...
    // Values under four characters are left, as are values already redacted. After a colon an unquoted value must hold
    // something other than letters, so prose like "the password: prompt" is left while "password: hunter2" is not.
    [GeneratedRegex(@"(?i)\b[A-Za-z0-9_.\-]*(?:password|passwd|secret|token|api[_\-]?key|access[_\-]?key|private[_\-]?key)[""']?\s*(?:=\s*[""']?|:\s*[""']|:\s*(?=[^\s""',;]*[^A-Za-z\s""',;]))(?!\[redacted:)(?<secret>[^\s""',;]{4,})", Options)]
    private static partial Regex SecretAssignment();
}
