using Protostar.Registry.Api.Common;
using Protostar.Registry.Api.Skills;

namespace Protostar.Registry.Api.Memory;

/// <summary>One memory file of a project, keyed by its path in the memory folder, with every distinct content it has had.</summary>
/// <remarks>
/// <para>
/// The aggregate root for agent memory backups (protostar-cli#45). A harness keeps memory per project, as a folder
/// of small markdown notes; the registry keeps each note by (owner, project, path) so the same note from every
/// machine and checkout lands in one place.
/// </para>
/// <para>
/// Versions are content-addressed: content already stored as any version of this file is never stored again, and
/// only new content takes the next number. Each machine's checkout that backs the file up has a
/// <see cref="MemoryFileCopy"/> saying which version it last sent and whether it has since been deleted there. A
/// deletion is only a marker on that copy: the file and all its versions stay, which is what makes this a backup.
/// </para>
/// </remarks>
public sealed class MemoryFile : AggregateRoot
{
    private readonly List<MemoryFileVersion> _versions = [];
    private readonly List<MemoryFileCopy> _copies = [];

    private MemoryFile()
    {
    }

    public Guid Id { get; private set; }

    public Guid OwnerId { get; private set; }

    /// <summary>The project the memory belongs to (ADR 0002).</summary>
    public Guid ProjectId { get; private set; }

    /// <summary>The file's path within the memory folder, for example <c>MEMORY.md</c>.</summary>
    public RelativePath Path { get; private set; } = default!;

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>When a version was added or a copy changed.</summary>
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Every distinct content the file has had, oldest first by <see cref="MemoryFileVersion.Number"/>.</summary>
    public IReadOnlyCollection<MemoryFileVersion> Versions => _versions;

    /// <summary>The checkouts that back the file up, one per machine and folder.</summary>
    public IReadOnlyCollection<MemoryFileCopy> Copies => _copies;

    /// <summary>Starts a file with no versions yet; <see cref="Record"/> adds the first.</summary>
    public static MemoryFile Create(Guid ownerId, Guid projectId, RelativePath path, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        OwnerId = ownerId,
        ProjectId = projectId,
        Path = path,
        CreatedAt = now,
        UpdatedAt = now,
    };

    /// <summary>Records <paramref name="content"/> as what the copy in <paramref name="folder"/> on <paramref name="machineId"/> holds now.</summary>
    /// <returns>What changed, and the version the copy now points at.</returns>
    /// <exception cref="MemoryFileTooLargeException">The content is over <see cref="MemoryFileSizePolicy.MaxBytes"/>.</exception>
    /// <remarks>
    /// Content equal to any earlier version, from any machine, points the copy at that version instead of adding one.
    /// A copy that was marked deleted and is sent again is present again.
    /// </remarks>
    public MemoryFileRecorded Record(Guid machineId, string folder, byte[] content, DateTimeOffset now)
    {
        MemoryFileSizePolicy.Check(Path, content.LongLength);

        var hash = Sha256Hash.Of(content);
        var version = _versions.FirstOrDefault(v => v.Sha256 == hash);
        var change = MemoryFileChange.Unchanged;
        if (version is null)
        {
            change = _versions.Count == 0 ? MemoryFileChange.Created : MemoryFileChange.Versioned;
            version = MemoryFileVersion.Create(Id, _versions.Count == 0 ? 1 : _versions.Max(v => v.Number) + 1, content, hash, machineId, now);
            _versions.Add(version);
            UpdatedAt = now;
        }

        var copy = CopyOn(machineId, folder);
        if (copy is null)
        {
            _copies.Add(MemoryFileCopy.Create(Id, machineId, folder, version.Number, now));
            UpdatedAt = now;
        }
        else if (copy.Holds(version.Number, now))
        {
            UpdatedAt = now;
        }

        return new MemoryFileRecorded(change, version.Number);
    }

    /// <summary>Marks the file deleted in <paramref name="folder"/> on <paramref name="machineId"/>, keeping it and its versions.</summary>
    /// <returns>
    /// <see cref="MemoryFileChange.MarkedDeleted"/>, <see cref="MemoryFileChange.Unchanged"/> when it already was, or
    /// <see cref="MemoryFileChange.Unknown"/> when that copy never backed the file up.
    /// </returns>
    public MemoryFileRecorded MarkDeleted(Guid machineId, string folder, DateTimeOffset now)
    {
        var copy = CopyOn(machineId, folder);
        if (copy is null)
            return new MemoryFileRecorded(MemoryFileChange.Unknown, null);
        if (!copy.MarkDeleted(now))
            return new MemoryFileRecorded(MemoryFileChange.Unchanged, copy.VersionNumber);

        UpdatedAt = now;
        return new MemoryFileRecorded(MemoryFileChange.MarkedDeleted, copy.VersionNumber);
    }

    private MemoryFileCopy? CopyOn(Guid machineId, string folder) =>
        _copies.FirstOrDefault(c => c.MachineId == machineId && c.Folder == folder);
}

/// <summary>One distinct content of a <see cref="MemoryFile"/>, stored verbatim.</summary>
public sealed class MemoryFileVersion
{
    private MemoryFileVersion()
    {
    }

    public Guid Id { get; private set; }

    public Guid MemoryFileId { get; private set; }

    /// <summary>Numbered per file from 1, in the order the registry first saw each content.</summary>
    public int Number { get; private set; }

    /// <summary>The file's bytes, verbatim.</summary>
    public byte[] Content { get; private set; } = default!;

    /// <summary>The size of <see cref="Content"/> in bytes, kept for listing without loading it.</summary>
    public long Size { get; private set; }

    /// <summary>SHA-256 of <see cref="Content"/>.</summary>
    public Sha256Hash Sha256 { get; private set; } = default!;

    /// <summary>The machine that first sent this content.</summary>
    public Guid MachineId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    internal static MemoryFileVersion Create(Guid memoryFileId, int number, byte[] content, Sha256Hash hash, Guid machineId, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        MemoryFileId = memoryFileId,
        Number = number,
        Content = (byte[])content.Clone(),
        Size = content.LongLength,
        Sha256 = hash,
        MachineId = machineId,
        CreatedAt = now,
    };
}

/// <summary>A checkout that backs a <see cref="MemoryFile"/> up: one machine and folder, the version it last sent, and whether it was deleted there.</summary>
/// <remarks>
/// Keyed by folder as well as machine because two clones of one repo on one machine are the same project but keep
/// separate memory folders. For a git checkout the folder is the main worktree, so its worktrees share one copy, as
/// they share one memory folder in the harness.
/// </remarks>
public sealed class MemoryFileCopy
{
    private MemoryFileCopy()
    {
    }

    public Guid Id { get; private set; }

    public Guid MemoryFileId { get; private set; }

    public Guid MachineId { get; private set; }

    /// <summary>The folder the memory belongs to on that machine: the main worktree for git, else the working directory.</summary>
    public string Folder { get; private set; } = default!;

    /// <summary>The version this copy last sent.</summary>
    public int VersionNumber { get; private set; }

    /// <summary>When the file was found deleted on this copy; <c>null</c> while it is present.</summary>
    public DateTimeOffset? DeletedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    internal static MemoryFileCopy Create(Guid memoryFileId, Guid machineId, string folder, int versionNumber, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        MemoryFileId = memoryFileId,
        MachineId = machineId,
        Folder = folder,
        VersionNumber = versionNumber,
        UpdatedAt = now,
    };

    /// <summary>Points the copy at <paramref name="versionNumber"/>, present. Returns whether anything changed.</summary>
    internal bool Holds(int versionNumber, DateTimeOffset now)
    {
        if (VersionNumber == versionNumber && DeletedAt is null)
            return false;
        VersionNumber = versionNumber;
        DeletedAt = null;
        UpdatedAt = now;
        return true;
    }

    /// <summary>Marks the copy deleted. Returns whether it was present.</summary>
    internal bool MarkDeleted(DateTimeOffset now)
    {
        if (DeletedAt is not null)
            return false;
        DeletedAt = now;
        UpdatedAt = now;
        return true;
    }
}

/// <summary>What recording or deleting a memory file did.</summary>
public enum MemoryFileChange
{
    /// <summary>The file was new to the registry; its first version was stored.</summary>
    Created,

    /// <summary>The content was new for this file; a new version was stored and the earlier ones kept.</summary>
    Versioned,

    /// <summary>Nothing new was stored: the content was already a version, or the copy was already marked deleted.</summary>
    Unchanged,

    /// <summary>The file was marked deleted on the sending checkout; it and its versions are kept.</summary>
    MarkedDeleted,

    /// <summary>A deletion for a file the sending checkout never backed up; nothing to mark.</summary>
    Unknown,
}

/// <summary>The outcome of <see cref="MemoryFile.Record"/> or <see cref="MemoryFile.MarkDeleted"/>.</summary>
/// <param name="Change">What changed.</param>
/// <param name="Version">The version the sending copy points at; <c>null</c> for <see cref="MemoryFileChange.Unknown"/>.</param>
public sealed record MemoryFileRecorded(MemoryFileChange Change, int? Version);
