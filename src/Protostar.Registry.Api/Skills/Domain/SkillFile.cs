namespace Protostar.Registry.Api.Skills;

/// <summary>
/// One file from a pushed skill, stored verbatim (never zipped) so evaluators can read it directly.
/// Belongs to a single <see cref="SkillVersion"/> and is created only from a validated
/// <see cref="SkillFileContent"/>, so its size and hash always match its bytes.
/// </summary>
public sealed class SkillFile
{
    private SkillFile()
    {
    }

    public Guid Id { get; private set; }

    public Guid SkillVersionId { get; private set; }

    /// <summary>The file path relative to the skill directory root, validated and normalized.</summary>
    public RelativePath RelativePath { get; private set; } = default!;

    /// <summary>The file's bytes, verbatim.</summary>
    public byte[] Content { get; private set; } = default!;

    /// <summary>The file size in bytes, kept alongside <see cref="Content"/> for cheap listing.</summary>
    public long Size { get; private set; }

    /// <summary>SHA-256 of <see cref="Content"/>.</summary>
    public Sha256Hash Sha256 { get; private set; } = default!;

    internal static SkillFile Create(Guid skillVersionId, SkillFileContent content) => new()
    {
        Id = Guid.NewGuid(),
        SkillVersionId = skillVersionId,
        RelativePath = content.Path,
        Content = content.Bytes,
        Size = content.Size,
        Sha256 = content.Hash,
    };
}
