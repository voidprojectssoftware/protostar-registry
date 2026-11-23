using YamlDotNet.RepresentationModel;

namespace Protostar.Registry.Api.Common;

/// <summary>Reads the YAML front matter of a Markdown document: the block between a leading <c>---</c> line and the next <c>---</c> or <c>...</c> line.</summary>
/// <remarks>
/// Shared by <see cref="Skills.SkillManifestParser"/> (a skill's SKILL.md) and <see cref="Agents.AgentManifestParser"/>
/// (a custom agent's file), which read different fields out of the same fence (ADR 0003). Lenient on shape: a UTF-8 BOM
/// and blank lines before the opening fence are tolerated.
/// </remarks>
public static class FrontMatter
{
    /// <summary>The front matter of <paramref name="markdown"/> as a YAML mapping, or <c>null</c> when there is none.</summary>
    /// <remarks>
    /// <c>null</c> when the document has no leading fence, the block is not closed, the YAML is malformed, or its root
    /// is not a mapping.
    /// </remarks>
    public static YamlMappingNode? Read(string markdown)
    {
        if (!TryExtract(markdown, out var yaml))
            return null;

        var stream = new YamlStream();
        try
        {
            stream.Load(new StringReader(yaml));
        }
        catch (YamlDotNet.Core.YamlException)
        {
            return null;
        }

        return stream.Documents.Count > 0 && stream.Documents[0].RootNode is YamlMappingNode root ? root : null;
    }

    /// <summary>The scalar value of <paramref name="key"/>, or <c>null</c> when it is absent or not a scalar.</summary>
    public static string? Scalar(YamlMappingNode root, string key) =>
        root.Children.TryGetValue(new YamlScalarNode(key), out var node) && node is YamlScalarNode scalar
            ? scalar.Value
            : null;

    /// <summary>The values of <paramref name="key"/> as a list: a YAML sequence of scalars, or one scalar split on commas.</summary>
    /// <remarks>Empty when the key is absent or holds anything else. Empty items are dropped.</remarks>
    public static IReadOnlyList<string> Sequence(YamlMappingNode root, string key)
    {
        if (!root.Children.TryGetValue(new YamlScalarNode(key), out var node))
            return [];

        return node switch
        {
            // A YAML sequence (block or flow) of scalars.
            YamlSequenceNode seq => seq.Children
                .OfType<YamlScalarNode>()
                .Select(s => s.Value)
                .OfType<string>()
                .Where(v => v.Length > 0)
                .ToList(),
            // A single scalar, or a comma-separated string, is accepted too.
            YamlScalarNode { Value: { Length: > 0 } value } => value
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList(),
            _ => [],
        };
    }

    private static bool TryExtract(string markdown, out string yaml)
    {
        yaml = string.Empty;
        if (string.IsNullOrEmpty(markdown))
            return false;

        var text = markdown.Replace("\r\n", "\n").Replace('\r', '\n');
        // Tolerate a UTF-8 BOM and leading blank lines before the opening fence.
        var start = 0;
        if (text.Length > 0 && text[0] == '﻿')
            start = 1;
        while (start < text.Length && (text[start] == '\n' || text[start] == ' ' || text[start] == '\t'))
            start++;

        var lines = text[start..].Split('\n');
        if (lines.Length == 0 || lines[0].Trim() != "---")
            return false;

        var body = new List<string>();
        for (var i = 1; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed is "---" or "...")
            {
                yaml = string.Join('\n', body);
                return true;
            }

            body.Add(lines[i]);
        }

        // No closing fence: not a valid front-matter block.
        return false;
    }
}
