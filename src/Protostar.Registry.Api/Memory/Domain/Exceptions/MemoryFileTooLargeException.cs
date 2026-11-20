namespace Protostar.Registry.Api.Memory;

/// <summary>Raised when a memory file is over <see cref="MemoryFileSizePolicy.MaxBytes"/>.</summary>
/// <remarks>The application service checks sizes before touching the aggregate, so this only fires on a path that skipped that check.</remarks>
public sealed class MemoryFileTooLargeException(string reason) : Exception(reason);
