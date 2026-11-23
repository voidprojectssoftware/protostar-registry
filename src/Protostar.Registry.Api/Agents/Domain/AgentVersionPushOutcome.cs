namespace Protostar.Registry.Api.Agents;

/// <summary>The result of <see cref="Agent.PushVersion"/>: a new version, or the current one when the push was identical to it.</summary>
public sealed class AgentVersionPushOutcome
{
    private AgentVersionPushOutcome(AgentVersion version, bool isUnchanged)
    {
        Version = version;
        IsUnchanged = isUnchanged;
    }

    public AgentVersion Version { get; }

    /// <summary>True when the push matched the current version, so no new version was created.</summary>
    public bool IsUnchanged { get; }

    public static AgentVersionPushOutcome Created(AgentVersion version) => new(version, isUnchanged: false);

    public static AgentVersionPushOutcome Unchanged(AgentVersion version) => new(version, isUnchanged: true);
}
