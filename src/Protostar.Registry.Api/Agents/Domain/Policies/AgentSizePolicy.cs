namespace Protostar.Registry.Api.Agents;

/// <summary>The domain rule that a pushed agent file stays under <see cref="MaxFileBytes"/>.</summary>
/// <remarks>
/// An agent is one Markdown file holding a system prompt, kept on its version row, so the limit is sized for a long
/// prompt rather than for a skill's bundle of files (ADR 0003).
/// </remarks>
public static class AgentSizePolicy
{
    /// <summary>Maximum size of an agent file, in bytes.</summary>
    public const long MaxFileBytes = 1024 * 1024;

    /// <summary><c>null</c> when <paramref name="size"/> is within the limit, otherwise the reason it is not.</summary>
    public static string? Exceeded(long size) =>
        size > MaxFileBytes ? $"The agent file is {size} bytes, over the {MaxFileBytes}-byte limit." : null;
}
