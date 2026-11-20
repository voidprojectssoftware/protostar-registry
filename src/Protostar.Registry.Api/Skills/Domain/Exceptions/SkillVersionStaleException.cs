namespace Protostar.Registry.Api.Skills;

/// <summary>Raised when a push names a base version that is no longer the skill's current version.</summary>
/// <remarks>
/// Ordinary input, not a programming error: two machines edited the skill from the same version and the other
/// pushed first. The push service translates it to a 409 so the client can show both sides instead of
/// silently stacking its stale edit on top.
/// </remarks>
public sealed class SkillVersionStaleException(int baseVersion, int currentVersion)
    : Exception($"This copy was based on version {baseVersion}, but the registry is at version {currentVersion}: the skill changed here and in the registry.")
{
    /// <summary>The version the push said it was based on.</summary>
    public int BaseVersion { get; } = baseVersion;

    /// <summary>The skill's current version when the push was refused.</summary>
    public int CurrentVersion { get; } = currentVersion;
}
