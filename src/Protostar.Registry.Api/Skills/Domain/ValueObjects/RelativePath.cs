namespace Protostar.Registry.Api.Skills;

/// <summary>
/// A file path relative to a skill directory root, guaranteed safe by construction: normalized to '/'
/// separators and proven to be neither empty, absolute/rooted, nor containing a <c>..</c> traversal
/// segment. Because the only way to obtain one is through validation, stored paths can never escape their
/// skill directory.
/// </summary>
public sealed record RelativePath
{
    private RelativePath(string value) => Value = value;

    public string Value { get; }

    /// <summary>
    /// Validates and normalizes <paramref name="raw"/>. Returns <c>false</c> (and leaves
    /// <paramref name="path"/> null) when the path is empty, absolute/rooted, or contains a traversal
    /// segment.
    /// </summary>
    public static bool TryCreate(string? raw, out RelativePath path)
    {
        path = null!;
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        // Normalize Windows and POSIX separators to '/' so the checks below are platform-independent.
        var candidate = raw.Replace('\\', '/').Trim();
        while (candidate.StartsWith("./", StringComparison.Ordinal))
            candidate = candidate[2..];

        if (candidate.Length == 0 || candidate.StartsWith('/') || candidate.Contains(':'))
            return false;

        var segments = candidate.Split('/');
        if (segments.Any(s => s is "" or "." or ".."))
            return false;

        path = new RelativePath(string.Join('/', segments));
        return true;
    }

    /// <summary>Rehydrates a path already validated when it was stored. For the persistence layer only.</summary>
    public static RelativePath FromTrusted(string value) => new(value);

    public override string ToString() => Value;
}
