namespace Protostar.Registry.Api.Skills;

/// <summary>
/// The open-standard fields parsed from a skill's SKILL.md YAML front matter
/// (<see href="https://agentskills.io/specification"/>). A value object: fields the manifest omits are
/// left null or empty; the registry stores whatever the author declared without inferring more.
/// </summary>
public sealed record SkillManifest
{
    /// <summary>A manifest with no declared fields, for a SKILL.md that has no (or an empty) front matter.</summary>
    public static readonly SkillManifest Empty = new();

    /// <summary>The skill's identifier (front-matter <c>name</c>); null when absent or blank.</summary>
    public string? Name { get; init; }

    /// <summary>What the skill does and when to use it (<c>description</c>); null when absent.</summary>
    public string? Description { get; init; }

    /// <summary>License name or bundled-file reference (<c>license</c>); null when absent.</summary>
    public string? License { get; init; }

    /// <summary>Environment requirements (<c>compatibility</c>); null when absent.</summary>
    public string? Compatibility { get; init; }

    /// <summary>Arbitrary string metadata (<c>metadata</c>); empty when absent.</summary>
    public IReadOnlyDictionary<string, string> Metadata { get; init; } =
        new Dictionary<string, string>();

    /// <summary>Tools the skill pre-approves (<c>allowed-tools</c>); empty when absent.</summary>
    public IReadOnlyList<string> AllowedTools { get; init; } = [];
}
