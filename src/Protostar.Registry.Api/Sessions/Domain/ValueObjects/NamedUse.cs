namespace Protostar.Registry.Api.Sessions;

/// <summary>One skill or custom agent a transcript invoked: its name as invoked, how many times, and when first and last.</summary>
/// <remarks>
/// Only the name and counts are kept, never the arguments it was invoked with (a skill's arguments, an agent's prompt):
/// they are free text the user or the model wrote, the likeliest place for pasted data. The name is exactly as the
/// harness recorded it, so a plugin's skill or agent keeps its <c>plugin:name</c> form. The times are <c>null</c> when
/// no invocation carried a timestamp. Skills and agents share the shape and the folding rules, but are kept in separate
/// lists (<see cref="TranscriptSummary.Skills"/>, <see cref="TranscriptSummary.Agents"/>), since a skill and an agent may share a name.
/// </remarks>
/// <param name="Name">The name as invoked.</param>
/// <param name="Uses">How many times it was invoked; at least 1.</param>
/// <param name="FirstUsedAt">The earliest invocation's timestamp.</param>
/// <param name="LastUsedAt">The latest invocation's timestamp.</param>
public sealed record NamedUse(string Name, int Uses, DateTimeOffset? FirstUsedAt, DateTimeOffset? LastUsedAt)
{
    /// <summary>The longest name kept; a longer one is not a name any harness writes, and is skipped.</summary>
    public const int MaxNameLength = 200;

    /// <summary>The most distinct names kept for one transcript, so a hostile transcript cannot grow its summary without limit.</summary>
    /// <remarks>Names past the limit are dropped; the ones already kept go on counting.</remarks>
    public const int MaxDistinct = 200;

    /// <summary>Folds <paramref name="more"/> into <paramref name="uses"/>: counts add up per name, and the times widen.</summary>
    /// <remarks>
    /// Names compare exactly, as skill and agent names do. The result keeps the order names were first seen in, and at most
    /// <see cref="MaxDistinct"/> names. Returns <paramref name="uses"/> itself when <paramref name="more"/> is empty.
    /// </remarks>
    public static IReadOnlyList<NamedUse> Merge(IReadOnlyList<NamedUse> uses, IReadOnlyList<NamedUse> more)
    {
        if (more.Count == 0)
            return uses;

        var merged = uses.ToList();
        foreach (var use in more)
        {
            var at = merged.FindIndex(u => u.Name == use.Name);
            if (at >= 0)
                merged[at] = merged[at].Add(use);
            else if (merged.Count < MaxDistinct)
                merged.Add(use);
        }

        return merged;
    }

    private NamedUse Add(NamedUse other) => this with
    {
        Uses = Uses + other.Uses,
        FirstUsedAt = Earliest(FirstUsedAt, other.FirstUsedAt),
        LastUsedAt = Latest(LastUsedAt, other.LastUsedAt),
    };

    /// <summary>One invocation of <paramref name="name"/> at <paramref name="at"/>.</summary>
    public static NamedUse Once(string name, DateTimeOffset? at) => new(name, 1, at, at);

    private static DateTimeOffset? Earliest(DateTimeOffset? a, DateTimeOffset? b) => a is null ? b : b is null ? a : a < b ? a : b;

    private static DateTimeOffset? Latest(DateTimeOffset? a, DateTimeOffset? b) => a is null ? b : b is null ? a : a > b ? a : b;
}
