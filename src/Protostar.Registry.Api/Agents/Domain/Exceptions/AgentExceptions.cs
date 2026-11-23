namespace Protostar.Registry.Api.Agents;

/// <summary>Raised when a user who is not an agent's creator attempts to change it (push, rename or delete).</summary>
/// <remarks>A guarded invariant: agents are namespaced per creator, so this signals a programming error, not input.</remarks>
public sealed class AgentCreatorViolationException(Guid agentId, Guid attemptedBy)
    : Exception($"User {attemptedBy} is not the creator of agent {agentId}, and only its creator can change it.")
{
    public Guid AgentId { get; } = agentId;

    public Guid AttemptedBy { get; } = attemptedBy;
}

/// <summary>Raised when an agent is renamed to a name another agent already has in the same creator and project.</summary>
/// <remarks>Ordinary input: the rename service translates it to a 409.</remarks>
public sealed class AgentNameTakenException(string name)
    : Exception($"There is already an agent named '{name}' here. Two version histories are never merged.")
{
    public string Name { get; } = name;
}

/// <summary>Raised when a pushed agent file exceeds <see cref="AgentSizePolicy"/>.</summary>
public sealed class AgentSizeExceededException(string reason) : Exception(reason);

/// <summary>Raised when a push names a base version that is no longer the agent's current version.</summary>
/// <remarks>Ordinary input: two machines edited the agent from the same version and the other pushed first. Translated to a 409.</remarks>
public sealed class AgentVersionStaleException(int baseVersion, int currentVersion)
    : Exception($"This copy was based on version {baseVersion}, but the registry is at version {currentVersion}: the agent changed here and in the registry.")
{
    /// <summary>The version the push said it was based on.</summary>
    public int BaseVersion { get; } = baseVersion;

    /// <summary>The agent's current version when the push was refused.</summary>
    public int CurrentVersion { get; } = currentVersion;
}
