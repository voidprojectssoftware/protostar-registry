using Protostar.Registry.Api.Common;

namespace Protostar.Registry.Api.Projects;

/// <summary>A full clone attached a remote to a project, so shallow clones of that remote now resolve to it.</summary>
public sealed record ProjectRemoteAttached(Guid OwnerId, Guid ProjectId, string Remote) : IDomainEvent;
