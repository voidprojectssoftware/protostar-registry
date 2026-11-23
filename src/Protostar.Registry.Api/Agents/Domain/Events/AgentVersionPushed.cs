using Protostar.Registry.Api.Common;

namespace Protostar.Registry.Api.Agents;

/// <summary>A new version of a custom agent was pushed.</summary>
/// <remarks>The seam later work hangs off (search indexing in 7.3), rather than being wired into the push path.</remarks>
public sealed record AgentVersionPushed(
    Guid AgentId, Guid VersionId, int VersionNumber, Guid PushedById, DateTimeOffset PushedAt) : IDomainEvent;
