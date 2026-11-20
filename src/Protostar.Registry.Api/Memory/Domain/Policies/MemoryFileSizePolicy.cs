using Protostar.Registry.Api.Skills;

namespace Protostar.Registry.Api.Memory;

/// <summary>The largest memory file the registry keeps.</summary>
/// <remarks>
/// <para>
/// Memory is short markdown notes: Claude Code keeps one fact per file plus a <c>MEMORY.md</c> index, and loads
/// only the start of that index into a session. Notes run to a few KiB. 256 KiB is far above any note an agent
/// writes, so a real one is never refused, while it keeps every version a cheap Postgres row and bounds a push
/// request.
/// </para>
/// <para>The CLI applies the same limit before uploading and reports a larger file as skipped, so this only catches other clients.</para>
/// </remarks>
public static class MemoryFileSizePolicy
{
    /// <summary>The largest file kept, in bytes.</summary>
    public const int MaxBytes = 256 * 1024;

    /// <summary>Throws <see cref="MemoryFileTooLargeException"/> when <paramref name="size"/> is over <see cref="MaxBytes"/>.</summary>
    public static void Check(RelativePath path, long size)
    {
        if (size > MaxBytes)
            throw new MemoryFileTooLargeException($"{path.Value} is {size} bytes; a memory file may be at most {MaxBytes / 1024} KiB.");
    }
}
