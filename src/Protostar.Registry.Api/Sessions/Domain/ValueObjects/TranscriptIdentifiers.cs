using System.Text.RegularExpressions;

namespace Protostar.Registry.Api.Sessions;

/// <summary>A harness id such as <c>claude-code</c> or <c>codex</c>, guaranteed lowercase letters, digits and dashes.</summary>
public sealed partial record HarnessId
{
    private HarnessId(string value) => Value = value;

    public string Value { get; }

    /// <summary>Validates <paramref name="raw"/>: 1 to 40 characters of <c>a-z</c>, <c>0-9</c> and <c>-</c>.</summary>
    public static bool TryCreate(string? raw, out HarnessId id)
    {
        id = null!;
        if (raw is null || !Pattern().IsMatch(raw))
            return false;
        id = new HarnessId(raw);
        return true;
    }

    /// <summary>Rehydrates a value already validated when it was stored. For the persistence layer only.</summary>
    public static HarnessId FromTrusted(string value) => new(value);

    public override string ToString() => Value;

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,39}$")]
    private static partial Regex Pattern();
}

/// <summary>
/// A transcript's id within its harness, chosen by the client from the harness's own ids: a session id for a
/// session, or <c>&lt;session id&gt;.&lt;agent id&gt;</c> for a subagent.
/// </summary>
/// <remarks>Limited to URL-safe characters so it can be a path segment without escaping.</remarks>
public sealed partial record TranscriptKey
{
    private TranscriptKey(string value) => Value = value;

    public string Value { get; }

    /// <summary>Validates <paramref name="raw"/>: 1 to 200 characters of letters, digits, <c>.</c>, <c>_</c> and <c>-</c>.</summary>
    public static bool TryCreate(string? raw, out TranscriptKey key)
    {
        key = null!;
        if (raw is null || !Pattern().IsMatch(raw))
            return false;
        key = new TranscriptKey(raw);
        return true;
    }

    /// <summary>Rehydrates a value already validated when it was stored. For the persistence layer only.</summary>
    public static TranscriptKey FromTrusted(string value) => new(value);

    public override string ToString() => Value;

    [GeneratedRegex("^[A-Za-z0-9_-][A-Za-z0-9._-]{0,199}$")]
    private static partial Regex Pattern();
}
