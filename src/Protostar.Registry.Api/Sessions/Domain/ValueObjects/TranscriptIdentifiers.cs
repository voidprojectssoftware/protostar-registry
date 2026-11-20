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

/// <summary>The start of a transcript key, as a person types it to find a session without the whole id.</summary>
/// <remarks>
/// <para>
/// Matching ignores case, so the prefix is kept lowercased in <see cref="Value"/>; it is compared with the stored key
/// lowercased the same way.
/// </para>
/// <para>
/// It must be at least <see cref="MinLength"/> characters. Session keys are UUIDs, so four hex characters split them
/// into 65,536 groups: a few sessions share a group even in a large backup, which is what a lookup that reports
/// ambiguity wants. Fewer characters would match a sixteenth or more of every session, which is a listing, not a
/// lookup. Four is also the shortest abbreviation git accepts for a commit.
/// </para>
/// </remarks>
public sealed record TranscriptKeyPrefix
{
    /// <summary>The fewest characters a prefix may have.</summary>
    public const int MinLength = 4;

    private TranscriptKeyPrefix(string value) => Value = value;

    /// <summary>The prefix, lowercased.</summary>
    public string Value { get; }

    /// <summary>
    /// Validates <paramref name="raw"/>: <see cref="MinLength"/> to 200 of the characters a <see cref="TranscriptKey"/> may
    /// hold, starting as a key may start.
    /// </summary>
    public static bool TryCreate(string? raw, out TranscriptKeyPrefix prefix)
    {
        prefix = null!;
        if (raw is null || raw.Length < MinLength || !TranscriptKey.TryCreate(raw, out _))
            return false;
        prefix = new TranscriptKeyPrefix(raw.ToLowerInvariant());
        return true;
    }

    public override string ToString() => Value;
}
