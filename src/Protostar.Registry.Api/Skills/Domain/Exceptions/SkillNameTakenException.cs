namespace Protostar.Registry.Api.Skills;

/// <summary>Raised when a skill is renamed to a name another skill already has in the same creator and project.</summary>
/// <remarks>Ordinary input, not a programming error: the rename service translates it to a 409.</remarks>
public sealed class SkillNameTakenException(string name)
    : Exception($"There is already a skill named '{name}' here. Two version histories are never merged.")
{
    public string Name { get; } = name;
}
