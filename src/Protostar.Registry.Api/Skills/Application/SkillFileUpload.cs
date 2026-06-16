namespace Protostar.Registry.Api.Skills;

/// <summary>
/// One uploaded file headed for storage: its path relative to the skill directory root and its bytes.
/// Path validation and normalization happen during ingestion, not here.
/// </summary>
public sealed record SkillFileUpload(string RelativePath, byte[] Content);
