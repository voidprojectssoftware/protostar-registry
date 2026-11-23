using Protostar.Registry.Api.Common;
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
        if (FrontMatter.Read(skillMarkdown) is not { } root)
            return null;

        return new SkillManifest
        {
            Name = Blank(FrontMatter.Scalar(root, "name")),
            Description = Blank(FrontMatter.Scalar(root, "description")),
            License = Blank(FrontMatter.Scalar(root, "license")),
            Compatibility = Blank(FrontMatter.Scalar(root, "compatibility")),
            Metadata = Map(root, "metadata"),
            AllowedTools = FrontMatter.Sequence(root, "allowed-tools"),
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
