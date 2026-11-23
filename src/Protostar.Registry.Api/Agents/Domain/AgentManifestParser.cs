using Protostar.Registry.Api.Common;

namespace Protostar.Registry.Api.Agents;

/// <summary>Reads an <see cref="AgentManifest"/> out of a custom agent's Markdown file.</summary>
/// <remarks>
/// <c>name</c> and <c>description</c> are required, as Claude Code requires them to load an agent. A file without a
/// <c>name</c> is refused rather than named after the file (ADR 0003): Claude Code would not load it either.
/// </remarks>
public static class AgentManifestParser
{
    /// <summary>The manifest of <paramref name="agentMarkdown"/>, or why it has none.</summary>
    /// <returns>The manifest and <c>null</c>, or <c>null</c> and a message saying what is missing.</returns>
    public static (AgentManifest? Manifest, string? Problem) Parse(string agentMarkdown)
    {
        if (FrontMatter.Read(agentMarkdown) is not { } root)
            return (null, "The agent file has no YAML front matter (a block between two '---' lines at the top) to read its name and description from.");

        var name = Blank(FrontMatter.Scalar(root, "name"));
        if (name is null)
            return (null, "The agent's front matter has no 'name'. Claude Code identifies an agent by its name, not its file name, so add 'name: ...'.");

        var description = Blank(FrontMatter.Scalar(root, "description"));
        if (description is null)
            return (null, $"The agent '{name}' has no 'description' in its front matter. Claude Code needs one to know when to use it.");

        return (new AgentManifest
        {
            Name = name,
            Description = description,
            Tools = FrontMatter.Sequence(root, "tools"),
            Model = Blank(FrontMatter.Scalar(root, "model")),
        }, null);
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
