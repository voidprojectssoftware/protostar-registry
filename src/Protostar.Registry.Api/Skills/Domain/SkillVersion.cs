using System.Text.Json;
using Protostar.Registry.Api.Identity;

namespace Protostar.Registry.Api.Skills;

/// <summary>
/// One immutable push of a <see cref="Skill"/>: the open-standard metadata parsed from its SKILL.md plus
/// the captured file set (<see cref="SkillFile"/>). Created only by the aggregate via
/// <see cref="Create"/>; versions are numbered per skill starting at 1.
/// </summary>
/// <remarks>
/// <see cref="MetadataJson"/> and <see cref="AllowedToolsJson"/> hold the corresponding SKILL.md fields
/// as JSON (a <c>jsonb</c> column): the registry stores them verbatim for later evaluators and does not
/// query into them.
/// </remarks>
public sealed class SkillVersion
{
    private readonly List<SkillFile> _files = [];

    private SkillVersion()
    {
    }

    public Guid Id { get; private set; }

    public Guid SkillId { get; private set; }

    /// <summary>Monotonic version number within the skill, starting at 1.</summary>
    public int VersionNumber { get; private set; }

    /// <summary>What the skill does and when to use it (SKILL.md <c>description</c>); null when omitted.</summary>
    public string? Description { get; private set; }

    /// <summary>License name or bundled-file reference (SKILL.md <c>license</c>); null when absent.</summary>
    public string? License { get; private set; }

    /// <summary>Environment requirements (SKILL.md <c>compatibility</c>); null when absent.</summary>
    public string? Compatibility { get; private set; }

    /// <summary>SKILL.md <c>metadata</c> as a JSON object; null when absent.</summary>
    public string? MetadataJson { get; private set; }

    /// <summary>SKILL.md <c>allowed-tools</c> as a JSON array; null when absent.</summary>
    public string? AllowedToolsJson { get; private set; }

    /// <summary>SHA-256 over this version's files; identical re-pushes share it, marking a no-op.</summary>
    public Sha256Hash ContentHash { get; private set; } = default!;

    /// <summary>The user who pushed this version.</summary>
    public Guid PushedById { get; private set; }

    public User PushedBy { get; private set; } = default!;

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>The files captured in this version, stored verbatim (never zipped).</summary>
    public IReadOnlyCollection<SkillFile> Files => _files;

    internal static SkillVersion Create(
        Guid skillId,
        int versionNumber,
        SkillManifest manifest,
        IReadOnlyList<SkillFileContent> files,
        Sha256Hash contentHash,
        Guid pushedBy,
        DateTimeOffset now)
    {
        var version = new SkillVersion
        {
            Id = Guid.NewGuid(),
            SkillId = skillId,
            VersionNumber = versionNumber,
            Description = manifest.Description,
            License = manifest.License,
            Compatibility = manifest.Compatibility,
            MetadataJson = ToJson(manifest.Metadata),
            AllowedToolsJson = ToJson(manifest.AllowedTools),
            ContentHash = contentHash,
            PushedById = pushedBy,
            CreatedAt = now,
        };

        foreach (var file in files.OrderBy(f => f.Path.Value, StringComparer.Ordinal))
            version._files.Add(SkillFile.Create(version.Id, file));

        return version;
    }

    private static string? ToJson(IReadOnlyDictionary<string, string> value) =>
        value.Count > 0 ? JsonSerializer.Serialize(value) : null;

    private static string? ToJson(IReadOnlyList<string> value) =>
        value.Count > 0 ? JsonSerializer.Serialize(value) : null;
}
