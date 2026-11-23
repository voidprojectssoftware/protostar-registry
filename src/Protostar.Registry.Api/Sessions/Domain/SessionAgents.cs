using Protostar.Registry.Api.Agents;

namespace Protostar.Registry.Api.Sessions;

/// <summary>A custom agent a session delegated to, matched against the owner's agents in the registry.</summary>
/// <param name="Name">The agent type as the session named it.</param>
/// <param name="Uses">How many times the session delegated to it.</param>
/// <param name="FirstUsedAt">The earliest delegation's timestamp, when one was recorded.</param>
/// <param name="LastUsedAt">The latest delegation's timestamp, when one was recorded.</param>
/// <param name="AgentId">The registry agent the name matched; <c>null</c> when the agent was deleted.</param>
/// <param name="ProjectId">The project of the agent matched; <c>null</c> for a personal agent, or one deleted.</param>
/// <param name="Version">The version inferred to be the one used; <c>null</c> when none had been pushed by the end of the session (allowing <see cref="SessionAgents.PushGrace"/>).</param>
/// <param name="Deleted">True when the name was a registry agent that has since been deleted.</param>
public sealed record SessionAgent(
    string Name, int Uses, DateTimeOffset? FirstUsedAt, DateTimeOffset? LastUsedAt, Guid? AgentId, Guid? ProjectId, int? Version, bool Deleted);

/// <summary>When one version of an agent was pushed: all the version inference needs, without the version's bytes.</summary>
public sealed record AgentVersionStamp(Guid AgentId, int VersionNumber, DateTimeOffset CreatedAt);

/// <summary>Picks the custom agents out of the agent types a session delegated to, and infers which version of each was used.</summary>
/// <remarks>
/// <para>
/// A transcript records every agent type a session delegated to, built-in (<c>general-purpose</c>, <c>Explore</c>,
/// <c>Plan</c>, ...) and plugin (<c>plugin:agent</c>) ones included. Only a name that matches one of the owner's agents,
/// or one of their removed agent names, is a custom agent, so only those are returned: the rest are dropped rather than
/// listed as unmatched, which is where this differs from <see cref="SessionSkills"/>. A plugin's agent never matches,
/// since plugins' agents are not pushed to the registry.
/// </para>
/// <para>
/// A name is looked up in the session's project first, then among the owner's personal agents, and the first scope
/// that knows the name decides: a project agent wins over a personal agent of the same name. That is Claude Code's
/// order for agents (project <c>.claude/agents/</c> before <c>~/.claude/agents/</c>), the reverse of its order for skills
/// (ADR 0003). Within a scope a live agent of that name matches; otherwise a removed name does: one renamed away matches
/// the agent under its new name, and one deleted is reported as deleted, with no agent or version.
/// </para>
/// <para>
/// The version is inferred, never known: it is the newest version pushed no later than <paramref name="endedAt"/> of
/// <see cref="Match"/> plus <see cref="PushGrace"/>. That is the end of the session, not the last use, unlike skills:
/// Claude Code loads agent files when a session starts, and the CLI backs agents up at <c>SessionEnd</c>, so an agent
/// created or edited before a session and used in it is pushed only when that session ends. Measuring from the last use
/// would report no version for an agent's first session, and the previous version for one edited outside a session. The
/// cost is the other way round: an agent edited during a session (which that session did not load) counts as the edited
/// version.
/// </para>
/// <para>
/// Matching happens when the agents are read, not when the transcript is parsed, so an agent pushed, renamed or deleted
/// later, or a session filed under its project later, is always reflected.
/// </para>
/// </remarks>
public static class SessionAgents
{
    /// <summary>How long after a session ends a pushed version still counts as the one it used.</summary>
    /// <remarks>
    /// The capture hook pushes changed agents at <c>SessionEnd</c>, in a detached process, a few seconds after the
    /// session's last line; the session's end is stamped by the client's clock and the version by the registry's. As
    /// <see cref="SessionSkills.PushGrace"/>, two minutes covers both.
    /// </remarks>
    public static readonly TimeSpan PushGrace = TimeSpan.FromMinutes(2);

    /// <summary>The custom agents among <paramref name="uses"/>, earliest first used first, then by name.</summary>
    /// <param name="uses">The agent types the session delegated to.</param>
    /// <param name="projectId">The session's project; <c>null</c> when it is not filed under one.</param>
    /// <param name="endedAt">When the session last did anything; <c>null</c> when no line carried a timestamp, which leaves every version unknown.</param>
    /// <param name="agents">The owner's agents that may match.</param>
    /// <param name="versions">When each version of those agents was pushed.</param>
    /// <param name="tombstones">The owner's removed agent names that may match.</param>
    public static IReadOnlyList<SessionAgent> Match(
        IReadOnlyList<NamedUse> uses, Guid? projectId, DateTimeOffset? endedAt, IReadOnlyList<Agent> agents,
        IReadOnlyList<AgentVersionStamp> versions, IReadOnlyList<AgentTombstone> tombstones)
    {
        Guid?[] scopes = projectId is { } project ? [project, null] : [null];
        return uses
            .Where(use => !IsPluginAgent(use.Name))
            .Select(use => Match(use, scopes, endedAt, agents, versions, tombstones))
            .OfType<SessionAgent>()
            .OrderBy(a => a.FirstUsedAt ?? DateTimeOffset.MaxValue)
            .ThenBy(a => a.Name, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>True when <paramref name="name"/> is a plugin's agent, which no registry agent can be.</summary>
    public static bool IsPluginAgent(string name) => name.Contains(':');

    private static SessionAgent? Match(
        NamedUse use, Guid?[] scopes, DateTimeOffset? endedAt, IReadOnlyList<Agent> agents, IReadOnlyList<AgentVersionStamp> versions,
        IReadOnlyList<AgentTombstone> tombstones)
    {
        foreach (var scope in scopes)
        {
            if (Find(agents, scope, use.Name) is { } live)
                return Matched(use, live, endedAt, versions);

            if (tombstones.FirstOrDefault(t => t.ProjectId == scope && t.Name == use.Name) is { } removed)
            {
                return removed.RenamedTo is { } renamedTo && Find(agents, scope, renamedTo) is { } renamed
                    ? Matched(use, renamed, endedAt, versions)
                    : new SessionAgent(use.Name, use.Uses, use.FirstUsedAt, use.LastUsedAt, null, null, null, Deleted: true);
            }
        }

        return null;
    }

    private static Agent? Find(IReadOnlyList<Agent> agents, Guid? scope, string name) =>
        agents.FirstOrDefault(a => a.ProjectId == scope && a.Name == name);

    private static SessionAgent Matched(NamedUse use, Agent agent, DateTimeOffset? endedAt, IReadOnlyList<AgentVersionStamp> versions)
    {
        var version = endedAt is { } at
            ? versions.Where(v => v.AgentId == agent.Id && v.CreatedAt <= at + PushGrace).MaxBy(v => v.VersionNumber)
            : null;
        return new SessionAgent(use.Name, use.Uses, use.FirstUsedAt, use.LastUsedAt, agent.Id, agent.ProjectId, version?.VersionNumber, Deleted: false);
    }
}
