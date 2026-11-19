using Protostar.Registry.Api.Common;

namespace Protostar.Registry.Api.Projects;

/// <summary>A folder was linked to a project or unlinked, so what its transcripts belong to may have changed.</summary>
/// <param name="OwnerId">The projects' owner.</param>
/// <param name="FolderKey">The folder's key, <c>&lt;machine id&gt;:&lt;path&gt;</c> (<see cref="ProjectIdentity.TryParseFolderKey"/>).</param>
public sealed record FolderLinkChanged(Guid OwnerId, string FolderKey) : IDomainEvent;
