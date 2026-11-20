using Protostar.Registry.Api.Common;

namespace Protostar.Registry.Api.Memory;

/// <summary>A memory file gained a version, or a copy of it changed version or was deleted, so its current version may differ.</summary>
/// <param name="OwnerId">The file's owner.</param>
/// <param name="MemoryFileId">The file.</param>
public sealed record MemoryFileChanged(Guid OwnerId, Guid MemoryFileId) : IDomainEvent;
