using YamlDotNet.RepresentationModel;

namespace Protostar.Registry.Api.Skills;

/// <summary>
/// Parses the open-standard fields out of a skill's SKILL.md YAML front matter. The registry, not the
/// pusher, is the source of truth for a pushed skill's metadata, so it reads the manifest server-side
/// rather than trusting client-supplied fields.
/// </summary>
public static class SkillManifestParser
{
    /// <summary>
    /// Extracts the open-standard fields from a SKILL.md document. Returns <c>null</c> when the document
    /// has no leading <c>---</c> front-matter block or the block is not a YAML mapping; unrecognized
    /// keys are ignored and absent fields are left null/empty.
    /// </summary>
    public static SkillManifest? Parse(string skillMarkdown)
    {
        if (!TryExtractFrontMatter(skillMarkdown, out var yaml))
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

        if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode root)
            return null;

        return new SkillManifest
        {
            Name = Blank(Scalar(root, "name")),
            Description = Blank(Scalar(root, "description")),
            License = Blank(Scalar(root, "license")),
            Compatibility = Blank(Scalar(root, "compatibility")),
            Metadata = Map(root, "metadata"),
            AllowedTools = Sequence(root, "allowed-tools"),
        };
    }

    // The front matter is the block delimited by a leading `---` line and the next `---` or `...` line.
    private static bool TryExtractFrontMatter(string markdown, out string yaml)
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

    private static string? Scalar(YamlMappingNode root, string key) =>
        root.Children.TryGetValue(new YamlScalarNode(key), out var node) && node is YamlScalarNode scalar
            ? scalar.Value
            : null;

    private static IReadOnlyList<string> Sequence(YamlMappingNode root, string key)
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

    private static IReadOnlyDictionary<string, string> Map(YamlMappingNode root, string key)
    {
        if (!root.Children.TryGetValue(new YamlScalarNode(key), out var node) ||
            node is not YamlMappingNode map)
        {
            return new Dictionary<string, string>();
        }

        var result = new Dictionary<string, string>();
        foreach (var (k, v) in map.Children)
        {
            if (k is YamlScalarNode { Value: { Length: > 0 } name } && v is YamlScalarNode { Value: { } value })
                result[name] = value;
        }

        return result;
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
