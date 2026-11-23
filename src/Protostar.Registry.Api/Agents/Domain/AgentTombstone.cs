namespace Protostar.Registry.Api.Agents;

/// <summary>What the registry remembers of an agent name that was deleted or renamed away: its scope, last version and new name.</summary>
/// <remarks>
/// <para>
/// Without it a push cannot tell "the registry never had this agent" from "someone deliberately removed it", so a machine
/// that still has an unedited copy would put the agent back (registry#58, copied for agents by ADR 0003). A push of that name whose base version is at
/// or below <see cref="LastVersion"/> came from a copy of the removed agent, and is refused (<see cref="Refuses"/>).
/// A push with base 0 (a copy that never synced a version) or no base (a client from before base versions) is not:
/// it creates a new agent of that name.
/// </para>
/// <para>
/// A tombstone exists only while its name has no live agent in its scope. When a new agent takes the name (pushed, or
/// renamed into it) the tombstone is dropped, and a new agent numbers its versions after <see cref="LastVersion"/>
/// (<see cref="Agent.Create"/>), so a copy of the removed agent can never pass for a copy of the new one: its base is
/// never the new agent's current version, and such a push is refused as stale.
/// </para>
/// </remarks>
public sealed class AgentTombstone
{
    private AgentTombstone()
    {
    }

    public Guid Id { get; private set; }

    /// <summary>The removed agent's creator, as on <see cref="Agent.CreatorId"/>.</summary>
    public Guid CreatorId { get; private set; }

    /// <summary>The removed agent's project; <c>null</c> for a personal agent.</summary>
    public Guid? ProjectId { get; private set; }

    /// <summary>The name that was removed.</summary>
    public string Name { get; private set; } = default!;

    /// <summary>The removed agent's current version when it was removed under this name.</summary>
    public int LastVersion { get; private set; }

    /// <summary>The agent's name now, when it was renamed; <c>null</c> when it was deleted.</summary>
    /// <remarks>
    /// Follows the agent through later renames, and becomes <c>null</c> when the agent is deleted under its new name, so
    /// it always names a live agent or none.
    /// </remarks>
    public string? RenamedTo { get; private set; }

    /// <summary>When the name was removed.</summary>
    public DateTimeOffset RemovedAt { get; private set; }

    /// <summary>The tombstone for <paramref name="agent"/>, which is about to be deleted.</summary>
    /// <param name="agent">The agent being deleted.</param>
    /// <param name="lastVersion">Its current version number.</param>
    /// <param name="now">When it is deleted.</param>
    public static AgentTombstone Deleted(Agent agent, int lastVersion, DateTimeOffset now) => For(agent, agent.Name, lastVersion, renamedTo: null, now);

    /// <summary>The tombstone for <paramref name="oldName"/>, which <paramref name="agent"/> has just been renamed from.</summary>
    /// <param name="agent">The agent, already carrying its new name.</param>
    /// <param name="oldName">The name it had.</param>
    /// <param name="lastVersion">Its current version number.</param>
    /// <param name="now">When it was renamed.</param>
    /// <exception cref="ArgumentException"><paramref name="oldName"/> is the agent's name, so nothing was renamed.</exception>
    public static AgentTombstone Renamed(Agent agent, string oldName, int lastVersion, DateTimeOffset now)
    {
        if (oldName == agent.Name)
            throw new ArgumentException("A rename tombstone is for the name the agent no longer has.", nameof(oldName));
        return For(agent, oldName, lastVersion, agent.Name, now);
    }

    private static AgentTombstone For(Agent agent, string name, int lastVersion, string? renamedTo, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        CreatorId = agent.CreatorId,
        ProjectId = agent.ProjectId,
        Name = name,
        LastVersion = lastVersion,
        RenamedTo = renamedTo,
        RemovedAt = now,
    };

    /// <summary>True when a push of this name based on <paramref name="baseVersion"/> came from a copy of the removed agent.</summary>
    /// <param name="baseVersion">The version the pushed copy last synced; 0 when it never synced one, <c>null</c> when the pusher does not say.</param>
    public bool Refuses(int? baseVersion) => baseVersion is { } based && based >= 1 && based <= LastVersion;

    /// <summary>The agent this name was renamed to has been renamed again, to <paramref name="newName"/>.</summary>
    public void FollowRename(string newName) => RenamedTo = newName;

    /// <summary>The agent this name was renamed to has been deleted, so the name now reads as deleted.</summary>
    public void TargetDeleted() => RenamedTo = null;
}
