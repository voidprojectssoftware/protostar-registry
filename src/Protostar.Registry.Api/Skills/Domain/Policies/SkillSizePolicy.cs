namespace Protostar.Registry.Api.Skills;

/// <summary>
/// The domain rule that a pushed skill version must stay within bounded size limits: no single file over
/// <see cref="MaxFileBytes"/>, and no version over <see cref="MaxVersionBytes"/> in total. Skills are small
/// text-and-asset bundles, and files are stored one row per file as Postgres <c>bytea</c>, so this both
/// reflects the intended shape of a skill and keeps a push within what the store can hold.
/// </summary>
public static class SkillSizePolicy
{
    /// <summary>Maximum size of any single file in a version, in bytes.</summary>
    public const long MaxFileBytes = 5 * 1024 * 1024;

    /// <summary>Maximum total size of all files in a version, in bytes.</summary>
    public const long MaxVersionBytes = 20 * 1024 * 1024;

    /// <summary>
    /// Returns <c>null</c> when <paramref name="files"/> are within the limits, otherwise a reason
    /// describing which limit was exceeded.
    /// </summary>
    public static string? Exceeded(IReadOnlyList<SkillFileContent> files)
    {
        foreach (var file in files)
        {
            if (file.Size > MaxFileBytes)
                return $"File '{file.Path.Value}' is {file.Size} bytes, over the {MaxFileBytes}-byte per-file limit.";
        }

        var total = files.Sum(file => file.Size);
        if (total > MaxVersionBytes)
            return $"The skill totals {total} bytes, over the {MaxVersionBytes}-byte per-skill limit.";

        return null;
    }
}
