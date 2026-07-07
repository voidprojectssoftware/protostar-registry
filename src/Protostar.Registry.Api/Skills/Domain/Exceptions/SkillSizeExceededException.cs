namespace Protostar.Registry.Api.Skills;

/// <summary>
/// Raised when a pushed skill version exceeds the size limits in <see cref="SkillSizePolicy"/>. Ordinary
/// input the caller can hit, so the application service translates it to a "too large" result rather than
/// letting it surface as an error.
/// </summary>
public sealed class SkillSizeExceededException(string reason) : Exception(reason);
