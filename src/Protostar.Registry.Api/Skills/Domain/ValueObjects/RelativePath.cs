namespace Protostar.Registry.Api.Skills;

/// <summary>
/// A file path relative to a skill directory root, guaranteed safe by construction: normalized to '/'
/// separators and proven to be neither empty, absolute/rooted, nor containing a <c>..</c> traversal
/// segment. Because the only way to obtain one is through validation, stored paths can never escape their
/// skill directory.
/// </summary>
/// <remarks>
/// Two more rules keep a stored skill identical to what a client reads back from disk after installing it.
/// A segment may not start or end with whitespace: such a path is refused rather than trimmed, because a
/// trimmed path would no longer match the file the client hashed. A segment may not be one of
/// <see cref="IgnoredSegments"/>, the version-control and dependency folders the CLI never reads from a
/// skill directory: a file stored under one would be written on install but never read back, so the two
/// sides could never agree on the skill's content.
/// </remarks>
public sealed record RelativePath
{
    /// <summary>
    /// Path segments that are never part of a skill, compared ignoring case. Mirrors the CLI's
    /// <c>SkillFileReader</c>, which skips these names when it reads a skill directory.
    /// </summary>
    public static readonly IReadOnlySet<string> IgnoredSegments =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".git", ".hg", ".svn", "node_modules", "__pycache__", ".venv" };

    private RelativePath(string value) => Value = value;

    public string Value { get; }

    /// <summary>
    /// Validates and normalizes <paramref name="raw"/>. Returns <c>false</c> (and leaves
    /// <paramref name="path"/> null) when the path is empty, absolute/rooted, contains a traversal
    /// segment, has a segment with leading or trailing whitespace, or has a segment in
    /// <see cref="IgnoredSegments"/>.
    /// </summary>
    public static bool TryCreate(string? raw, out RelativePath path)
    {
        path = null!;
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        // Normalize Windows and POSIX separators to '/' so the checks below are platform-independent.
        var candidate = raw.Replace('\\', '/');
        while (candidate.StartsWith("./", StringComparison.Ordinal))
            candidate = candidate[2..];

        if (candidate.Length == 0 || candidate.StartsWith('/') || candidate.Contains(':'))
            return false;

        var segments = candidate.Split('/');
        if (segments.Any(s => s is "" or "." or ".." || s.Trim() != s || IgnoredSegments.Contains(s)))
            return false;

        path = new RelativePath(string.Join('/', segments));
        return true;
    }

    /// <summary>Rehydrates a path already validated when it was stored. For the persistence layer only.</summary>
    public static RelativePath FromTrusted(string value) => new(value);

    public override string ToString() => Value;
}
