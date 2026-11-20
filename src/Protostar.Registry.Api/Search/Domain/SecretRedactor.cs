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
/// too, line by line, by way of <see cref="JsonLineRedactor"/>, and so is memory content downloaded with <c>redacted=true</c>.
/// </para>
/// <para>
/// The pattern set is curated, not exhaustive: each pattern matches a format with a recognisable shape (a fixed
/// prefix, a fixed length, a PEM header) or a value assigned to a name that says it is secret. Plain hex (commit
/// hashes), GUIDs and base64 blobs such as inline images have no such shape and are left alone. Patterns run in
/// order, most specific first, and the catch-all assignment pattern skips values that are already markers, so a
/// <c>token=ghp_...</c> keeps its <c>github-token</c> type.
/// </para>
/// <para>
/// A value assigned to a secret name is replaced whole: a quoted one through its closing quote, an unquoted one to the
/// end of its line, spaces and semicolons included. Names and quotes are read through JSON escapes to any depth, so the
/// password in <c>curl -d "{\"password\":\"...\"}"</c> is caught wherever the text sits.
/// </para>
/// <para>
/// Every pattern runs in time linear in the text, and every pattern has a <see cref="MatchTimeout"/> behind that; text
/// that runs a pattern out of time is replaced whole by <see cref="Unscannable"/>, never returned as it was.
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
    /// <remarks>
    /// Text with no secrets comes back unchanged (the same instance). Redacting already redacted text changes nothing.
    /// Text a pattern cannot finish scanning within <see cref="MatchTimeout"/> comes back as the single marker
    /// <c>[redacted:unscannable]</c>, never as it was: failing closed costs readability, failing open could leak.
    /// </remarks>
    public static string Redact(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        try
        {
            foreach (var pattern in _addedForTests is null ? All : [.. All, _addedForTests])
            {
                if (!_prefiltersOff && pattern.Prefilter.Skips(text))
                    continue;

                text = pattern.Group is null
                    ? pattern.Regex.Replace(text, pattern.Marker)
                    : pattern.Regex.Replace(text, pattern.Evaluator);
            }
        }
        catch (RegexMatchTimeoutException)
        {
            return Unscannable;
        }

        return text;
    }

    /// <summary>The marker that replaces the whole of a text the patterns could not finish scanning.</summary>
    public static string Unscannable { get; } = Marker("unscannable");

    /// <summary>The longest one pattern may spend on one text before the text counts as unscannable.</summary>
    /// <remarks>
    /// Every pattern runs in time linear in the text (the unit tests hold 1 MB adversarial inputs to that), at tens of
    /// MB/s, so a 16 MiB transcript chunk takes well under this. The timeout is defence in depth against a pattern
    /// that turns out not to be linear on some input: it bounds the CPU one request can take.
    /// </remarks>
    public static TimeSpan MatchTimeout { get; } = TimeSpan.FromMilliseconds(MatchTimeoutMilliseconds);

    private const int MatchTimeoutMilliseconds = 5_000;

    /// <summary>True when <paramref name="name"/> (a property name, a variable) says the value it names is a secret.</summary>
    /// <remarks>
    /// The same names the assignment pattern recognises: ending in password, passwd, secret, token, api key, access key
    /// or private key, in any case, so <c>DB_PASSWORD</c>, <c>accessToken</c> and <c>x-api-key</c> are, while
    /// <c>max_tokens</c> and <c>secretName</c> are not.
    /// </remarks>
    public static bool IsSecretName(string name)
    {
        try
        {
            return SecretName().IsMatch(name);
        }
        catch (RegexMatchTimeoutException)
        {
            return true;
        }
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

    [ThreadStatic]
    private static Pattern? _addedForTests;

    /// <summary>Runs <paramref name="regex"/> after the patterns, on this thread, until the result is disposed; for tests.</summary>
    /// <remarks>Lets a test give the redactor a pattern that times out, to see it fail closed. Synchronous callers only.</remarks>
    internal static IDisposable WithAddedPattern(Regex regex)
    {
        _addedForTests = new Pattern("test", regex, Prefilter.None);
        return new AddedPatternGone();
    }

    private sealed class AddedPatternGone : IDisposable
    {
        public void Dispose() => _addedForTests = null;
    }

    /// <summary>Each pattern's type and regex, in the order the patterns run; for tests.</summary>
    internal static IReadOnlyList<(string Type, Regex Regex)> Regexes { get; } = All.Select(p => (p.Type, p.Regex)).ToList();

    // Order matters; see the class remarks.
    private static IEnumerable<Pattern> Patterns()
    {
        var none = Prefilter.None;
        yield return new("private-key", PrivateKey(), none);
        yield return new("jwt", Jwt(), none);
        yield return new("bearer-token", BearerToken(), Prefilter.AnyOf(StringComparison.OrdinalIgnoreCase, "bearer"), "secret");
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

    // Every pattern below runs in time linear in its input, so a pasted megabyte cannot hold a request's thread for
    // minutes (registry#80). Two habits keep it so. A pattern that can fail after scanning a run of characters starts
    // only where such a run starts (a lookbehind, not \b, since a dotted or dashed run has a \b at every dot), so no
    // two attempts scan the same run. And a loop that need not give characters back is atomic (?>...), or is followed
    // by something it cannot also match, so a failure never retries every shorter length.

    // A PEM private key block of any kind (RSA, EC, OPENSSH, ENCRYPTED, PKCS#8). A block cut off before its END line
    // (a truncated paste) is redacted through the end of its base64 body, including JSON-escaped "\n" separators. The
    // search for the END line stops at the next BEGIN, so many unterminated headers cost one pass, not one per header.
    [GeneratedRegex(@"-----BEGIN [A-Z0-9 ]*PRIVATE KEY-----(?:(?:(?!-----BEGIN )[\s\S])*?-----END [A-Z0-9 ]*PRIVATE KEY-----|(?:[A-Za-z0-9+/=\r\n]|\\[nr])*)", Options, MatchTimeoutMilliseconds)]
    private static partial Regex PrivateKey();

    // Three base64url segments, the first two JSON objects ("eyJ" is base64 for '{"').
    [GeneratedRegex(@"(?<![A-Za-z0-9_\-])eyJ[A-Za-z0-9_\-]{8,}\.eyJ[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]{8,}", Options, MatchTimeoutMilliseconds)]
    private static partial Regex Jwt();

    // An opaque bearer token, as in an Authorization header: "Bearer " and then at least 16 token characters, one of
    // them a digit, so prose such as "Bearer authentication" is left. A JWT has already become a marker by now.
    [GeneratedRegex(@"(?i)\bBearer[ \t]+(?=(?>[A-Za-z_.~+/\-]*)[0-9])(?<secret>[A-Za-z0-9_.~+/\-]{16,}=*)", Options, MatchTimeoutMilliseconds)]
    private static partial Regex BearerToken();

    // scheme://user:password@host: only the password. The scheme is not matched: "://" alone says it is a URL.
    [GeneratedRegex(@"://[^\s/:@""']+:(?<secret>[^\s@/""']+)@", Options, MatchTimeoutMilliseconds)]
    private static partial Regex UrlPassword();

    // ADO.NET, ODBC and libpq keyword connection strings: Password=...; Pwd=... A semicolon before the keyword or after
    // the value is what makes it a connection string; a lone password=... is left to the assignment pattern. The value
    // runs to the semicolon that starts the next keyword, so a password holding a semicolon is replaced whole.
    [GeneratedRegex(@"(?i)(?:(?<=;\s*)(?:password|pwd)\s*=\s*(?!\[redacted:)" + ConnectionStringValue + @"|(?<![A-Za-z0-9_])(?:password|pwd)\s*=\s*(?!\[redacted:)" + ConnectionStringValue + @"(?=\s*;))", Options, MatchTimeoutMilliseconds)]
    private static partial Regex ConnectionStringPassword();

    private const string ConnectionStringValue = @"(?<secret>(?>[^;\s""']+(?:;(?!\s*[A-Za-z][A-Za-z0-9 _]*=)[^;\s""']+)*))";

    // Azure Storage connection strings: AccountKey=<base64>.
    [GeneratedRegex(@"(?i)\bAccountKey\s*=\s*(?<secret>[A-Za-z0-9+/]{20,}={0,2})", Options, MatchTimeoutMilliseconds)]
    private static partial Regex AzureStorageKey();

    // Azure SAS tokens: the sig= parameter of a signed URL or query.
    [GeneratedRegex(@"[?&]sig=(?<secret>[A-Za-z0-9%+/=]{16,})", Options, MatchTimeoutMilliseconds)]
    private static partial Regex AzureSasSignature();

    // Microsoft Entra ID (Azure AD) client secrets: three characters, a digit, "Q~", then 31 to 34 more.
    [GeneratedRegex(@"(?<![A-Za-z0-9_~.\-])[A-Za-z0-9_~.\-]{3}\dQ~[A-Za-z0-9_~.\-]{31,34}(?![A-Za-z0-9_~.\-])", Options, MatchTimeoutMilliseconds)]
    private static partial Regex AzureClientSecret();

    // AWS access key ids: a four-letter prefix naming the key kind, then 16 uppercase letters or digits.
    [GeneratedRegex(@"\b(?:AKIA|ASIA|ABIA|ACCA|AGPA|AIDA|AROA|ANPA|ANVA|AIPA)[A-Z0-9]{16}\b", Options, MatchTimeoutMilliseconds)]
    private static partial Regex AwsAccessKey();

    // AWS secret access keys have no prefix, only a length (40), so only one assigned to a name that says what it is.
    [GeneratedRegex(@"(?i)\baws_?secret(?:_?access)?_?key[""']?\s*[:=]\s*[""']?(?<secret>[A-Za-z0-9/+]{40})(?![A-Za-z0-9/+])", Options, MatchTimeoutMilliseconds)]
    private static partial Regex AwsSecretKey();

    // GitHub tokens: classic personal (ghp_), OAuth (gho_), user-to-server (ghu_), server-to-server (ghs_), refresh
    // (ghr_), and fine-grained personal tokens (github_pat_). A classic token's body is letters and digits only, so it
    // ends at an underscore that follows it (ghp_..._suffix), which \b would not see as an end.
    [GeneratedRegex(@"(?<![A-Za-z0-9])(?:gh[pousr]_[A-Za-z0-9]{36,255}(?![A-Za-z0-9])|github_pat_[A-Za-z0-9_]{22,255}(?![A-Za-z0-9_]))", Options, MatchTimeoutMilliseconds)]
    private static partial Regex GitHubToken();

    [GeneratedRegex(@"\bsk-ant-[A-Za-z0-9_\-]{20,}", Options, MatchTimeoutMilliseconds)]
    private static partial Regex AnthropicKey();

    // OpenAI keys: sk-, sk-proj-, sk-svcacct-, sk-admin-. A digit is required so a long kebab-case name starting
    // "sk-" is not taken for a key.
    [GeneratedRegex(@"(?<![A-Za-z0-9_\-])sk-(?=(?>[A-Za-z_\-]*)[0-9])[A-Za-z0-9_\-]{20,}", Options, MatchTimeoutMilliseconds)]
    private static partial Regex OpenAiKey();

    // Google API keys (Maps, Gemini, Firebase and other GCP APIs).
    [GeneratedRegex(@"\bAIza[0-9A-Za-z_\-]{35}(?![0-9A-Za-z_\-])", Options, MatchTimeoutMilliseconds)]
    private static partial Regex GoogleApiKey();

    [GeneratedRegex(@"\bxox[abposr]-[A-Za-z0-9\-]{10,}", Options, MatchTimeoutMilliseconds)]
    private static partial Regex SlackToken();

    [GeneratedRegex(@"\b(?:sk|rk)_live_[A-Za-z0-9]{16,}", Options, MatchTimeoutMilliseconds)]
    private static partial Regex StripeKey();

    // The words a secret's name ends with. The assignment pattern and IsSecretName share them.
    private const string SecretNameWords = @"password|passwd|secret|token|api[_\-]?key|access[_\-]?key|private[_\-]?key";

    // Quotes may be escaped to any depth up to 15 backslashes (four levels of JSON in a string), as in a curl -d body
    // inside a Bash command: \"password\":\"...\". A quote may also be a \u0022 or \u0027 escape, as .NET's JSON
    // writer spells it, behind the same backslashes.
    private const string AnyQuote = @"(?:\\{0,15}[""']|\\{1,15}u00(?:22|27))";

    // A quoted value, replaced whole through its closing quote: the same quote character behind the same number of
    // backslashes as the opening one. Values under four characters, and a value that is already a marker (perhaps with
    // the line break a PEM key ends with), are left. A value with no closing quote runs to the end of the line.
    private const string QuotedValue =
        @"(?<esc>\\{0,15})(?<q>[""']|(?<=\\)u00(?:22|27))(?!\[redacted:[a-z-]+\](?:\s|\\[nrt])*\k<esc>\k<q>)(?<secret>(?>(?:(?!(?<!\\)\k<esc>\k<q>)[^\r\n]){4,}))";

    // An unquoted value, replaced through the end of the line, spaces and semicolons included, stopping at a quote, a
    // comma or an escape sequence. Its first word must be at least four characters. A later word holding a colon or an
    // equals sign is the next assignment (password=x token=y), not more of this value, so the value stops before it.
    private const string UnquotedValue =
        @"(?<secret>(?=[^\s""',\\]{4})(?>(?:[^\s""',\\]|\\(?![\\""'nrtu]))+(?:[ \t]+(?>(?:[^\s""',\\:=]|\\(?![\\""'nrtu]))+)(?![:=]))*))";

    // A value assigned to a name that says it is secret: password=..., "client_secret": "...", API_KEY: ..., and the
    // Kubernetes-style pair {"name": "DB_PASSWORD", "value": "..."}. After a colon an unquoted value's first word must
    // hold something other than letters, so prose like "the password: prompt" is left while "password: hunter2" is not.
    // The match starts at the word, not at the start of the name: a name's other characters are kept anyway, and
    // scanning back over them from every word boundary of a long dotted run is what made this pattern quadratic.
    [GeneratedRegex(
        @"(?i)(?:" + SecretNameWords + @")(?:" + AnyQuote + @")?\s*(?:[:=]\s*" + QuotedValue
            + @"|=\s*(?!\[redacted:)" + UnquotedValue
            + @"|:\s*(?=(?>[A-Za-z]*)[^A-Za-z\s""',;\\])(?!\[redacted:)" + UnquotedValue + @")"
        + @"|(?<![A-Za-z])(?:name|key)" + AnyQuote + @"\s*:\s*" + AnyQuote + @"[A-Za-z0-9_.\-]*(?:" + SecretNameWords + @")"
            + AnyQuote + @"\s*,\s*" + AnyQuote + @"value" + AnyQuote + @"\s*:\s*" + QuotedValue,
        Options, MatchTimeoutMilliseconds)]
    private static partial Regex SecretAssignment();

    [GeneratedRegex(@"(?i)(?:" + SecretNameWords + @")\z", Options, MatchTimeoutMilliseconds)]
    private static partial Regex SecretName();
}
