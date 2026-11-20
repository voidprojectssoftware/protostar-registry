using Microsoft.EntityFrameworkCore;
using Npgsql;
using Protostar.Registry.Api.Infrastructure;
using Protostar.Registry.Api.Projects;
using Protostar.Registry.Api.Skills;

namespace Protostar.Registry.Api.Memory;

/// <summary>The bounds a memory push is checked against.</summary>
public static class MemoryLimits
{
    /// <summary>The most files, sent and deleted together, one push may carry.</summary>
    public const int MaxFilesPerPush = 100;

    /// <summary>The largest push request, in bytes: <see cref="MaxFilesPerPush"/> files at the size limit, base64-encoded, plus room for the rest.</summary>
    public const long MaxRequestBytes = (MaxFilesPerPush * (long)MemoryFileSizePolicy.MaxBytes * 4 / 3) + (1024 * 1024);

    /// <summary>The longest path within a memory folder.</summary>
    public const int MaxPathLength = 500;

    /// <summary>The longest folder a copy is filed by, as for project facts.</summary>
    public const int MaxFolderLength = 2000;
}

/// <summary>Application service for memory backups: records pushed memory files and lists what is kept.</summary>
/// <remarks>
/// Every operation is scoped to <c>ownerId</c>. A push names its project by facts, which resolve through the same
/// <see cref="ProjectService"/> matching that files sessions, so a folder's memory and its sessions always share a
/// project (ADR 0002).
/// </remarks>
public sealed class MemoryService
{
    private readonly RegistryDbContext _db;
    private readonly ProjectService _projects;

    public MemoryService(RegistryDbContext db, ProjectService projects)
    {
        _db = db;
        _projects = projects;
    }

    /// <summary>Records the changed and deleted memory files of one project folder on one machine.</summary>
    /// <remarks>
    /// All files are saved in one transaction. Two pushes creating the same file or version at once collide on a
    /// unique index; the loser forgets its changes and applies them again over the winner's.
    /// </remarks>
    public async Task<MemoryResult<MemoryPushResult>> PushAsync(Guid ownerId, MemoryPushRequest request, CancellationToken cancellationToken)
    {
        if (request.Project is null)
            return MemoryResult<MemoryPushResult>.Invalid("project is required: the facts of the folder the memory belongs to.");
        if (request.Project.Problem() is { } badFacts)
            return MemoryResult<MemoryPushResult>.Invalid(badFacts);

        var files = request.Files ?? [];
        var deleted = request.Deleted ?? [];
        if (files.Count + deleted.Count > MemoryLimits.MaxFilesPerPush)
            return MemoryResult<MemoryPushResult>.Invalid($"A push may carry at most {MemoryLimits.MaxFilesPerPush} files, sent and deleted together.");

        var paths = new List<RelativePath>();
        foreach (var raw in files.Select(f => f.Path).Concat(deleted))
        {
            if (raw is null || raw.Length > MemoryLimits.MaxPathLength || !RelativePath.TryCreate(raw, out var path))
                return MemoryResult<MemoryPushResult>.Invalid($"'{raw}' is not a valid relative path of at most {MemoryLimits.MaxPathLength} characters.");
            paths.Add(path);
        }

        if (paths.Select(p => p.Value).Distinct(StringComparer.Ordinal).Count() != paths.Count)
            return MemoryResult<MemoryPushResult>.Invalid("Each path may appear once per push, as a file or as deleted.");
        if (files.FirstOrDefault(f => f.Content is null) is { } empty)
            return MemoryResult<MemoryPushResult>.Invalid($"{empty.Path} has no content.");
        if (files.FirstOrDefault(f => f.Content!.LongLength > MemoryFileSizePolicy.MaxBytes) is { } large)
            return MemoryResult<MemoryPushResult>.TooLarge($"{large.Path} is {large.Content!.LongLength} bytes; a memory file may be at most {MemoryFileSizePolicy.MaxBytes / 1024} KiB.");

        if (!await _db.Machines.AnyAsync(m => m.OwnerId == ownerId && m.Id == request.MachineId, cancellationToken))
            return MemoryResult<MemoryPushResult>.UnknownMachine($"Machine {request.MachineId} is not registered. Register it with PUT /v1/machines/{{machineId}} first.");

        // Resolve first: resolution saves any new project and may reset the change tracker on a race.
        var identity = ProjectIdentity.From(request.MachineId, request.Project);
        var project = await _projects.ResolveAsync(ownerId, identity, cancellationToken);
        var sent = files.Zip(paths).Select(p => (Path: p.Second, Content: p.First.Content!)).ToList();
        var gone = paths.Skip(files.Count).ToList();

        for (var attempt = 1; ; attempt++)
        {
            var outcomes = await ApplyAsync(ownerId, project.ProjectId, request.MachineId, identity.Folder, sent, gone, cancellationToken);
            try
            {
                if (attempt < 3)
                    await _db.SaveChangesRacingAsync(cancellationToken);
                else
                    await _db.SaveChangesAsync(cancellationToken);
                return MemoryResult<MemoryPushResult>.Ok(new MemoryPushResult(project.ProjectId, project.Name, identity.Folder, outcomes));
            }
            catch (DbUpdateException ex) when (attempt < 3 && ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                // A concurrent push stored one of these files or versions first. Apply again over what it stored.
                _db.ChangeTracker.Clear();
            }
        }
    }

    private async Task<List<MemoryFileOutcome>> ApplyAsync(
        Guid ownerId, Guid projectId, Guid machineId, string folder,
        IReadOnlyList<(RelativePath Path, byte[] Content)> sent, IReadOnlyList<RelativePath> gone, CancellationToken cancellationToken)
    {
        var wanted = sent.Select(s => s.Path).Concat(gone).ToList();
        var existing = await _db.MemoryFiles
            .Include(f => f.Versions)
            .Include(f => f.Copies)
            .Where(f => f.OwnerId == ownerId && f.ProjectId == projectId && wanted.Contains(f.Path))
            .ToDictionaryAsync(f => f.Path.Value, StringComparer.Ordinal, cancellationToken);

        var now = DateTimeOffset.UtcNow;
        var outcomes = new List<MemoryFileOutcome>(wanted.Count);
        foreach (var (path, content) in sent)
        {
            if (!existing.TryGetValue(path.Value, out var file))
                file = _db.MemoryFiles.Add(MemoryFile.Create(ownerId, projectId, path, now)).Entity;
            var recorded = file.Record(machineId, folder, content, now);
            outcomes.Add(new MemoryFileOutcome(path.Value, recorded.Change, recorded.Version));
        }

        foreach (var path in gone)
        {
            var recorded = existing.TryGetValue(path.Value, out var file)
                ? file.MarkDeleted(machineId, folder, now)
                : new MemoryFileRecorded(MemoryFileChange.Unknown, null);
            outcomes.Add(new MemoryFileOutcome(path.Value, recorded.Change, recorded.Version));
        }

        return outcomes;
    }

    /// <summary>The owner's projects that have memory, with counts, most recently changed first.</summary>
    public async Task<IReadOnlyList<MemoryProjectSummary>> ListProjectsAsync(Guid ownerId, CancellationToken cancellationToken)
    {
        var groups = await _db.MemoryFiles.AsNoTracking()
            .Where(f => f.OwnerId == ownerId)
            .GroupBy(f => f.ProjectId)
            .Select(g => new
            {
                ProjectId = g.Key,
                Files = g.Count(),
                Versions = g.Sum(f => f.Versions.Count),
                UpdatedAt = g.Max(f => f.UpdatedAt),
            })
            .ToListAsync(cancellationToken);

        var ids = groups.Select(g => g.ProjectId).ToList();
        var names = await _db.Projects.AsNoTracking()
            .Where(p => ids.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => p.Name, cancellationToken);
        var machines = await _db.MemoryFiles.AsNoTracking()
            .Where(f => f.OwnerId == ownerId)
            .SelectMany(f => f.Copies.Select(c => new { f.ProjectId, c.MachineId }))
            .Distinct()
            .ToListAsync(cancellationToken);

        return groups
            .Select(g => new MemoryProjectSummary(
                g.ProjectId, names.GetValueOrDefault(g.ProjectId) ?? "", g.Files, g.Versions,
                machines.Count(m => m.ProjectId == g.ProjectId), g.UpdatedAt))
            .OrderByDescending(s => s.UpdatedAt)
            .ToList();
    }

    /// <summary>Every memory file of one of the owner's projects, with its versions and copies but no content; <c>null</c> when the project is not the owner's.</summary>
    public async Task<MemoryProjectFiles?> ListFilesAsync(Guid ownerId, Guid projectId, CancellationToken cancellationToken)
    {
        var name = await _db.Projects.AsNoTracking()
            .Where(p => p.OwnerId == ownerId && p.Id == projectId)
            .Select(p => p.Name)
            .SingleOrDefaultAsync(cancellationToken);
        if (name is null)
            return null;

        // Projected, so listing never loads file content.
        var files = await _db.MemoryFiles.AsNoTracking()
            .Where(f => f.OwnerId == ownerId && f.ProjectId == projectId)
            .Select(f => new
            {
                f.Path,
                f.UpdatedAt,
                Versions = f.Versions.OrderBy(v => v.Number).Select(v => new MemoryVersionView(v.Number, v.Sha256.Value, v.Size, v.MachineId, v.CreatedAt)).ToList(),
                Copies = f.Copies.Select(c => new { c.MachineId, c.Folder, c.VersionNumber, c.DeletedAt, c.UpdatedAt }).ToList(),
            })
            .ToListAsync(cancellationToken);

        var machineIds = files.SelectMany(f => f.Copies.Select(c => c.MachineId)).Distinct().ToList();
        var machineNames = await _db.Machines.AsNoTracking()
            .Where(m => m.OwnerId == ownerId && machineIds.Contains(m.Id))
            .ToDictionaryAsync(m => m.Id, m => m.Name, cancellationToken);

        return new MemoryProjectFiles(
            projectId,
            name,
            files.OrderBy(f => f.Path.Value, StringComparer.Ordinal)
                .Select(f => new MemoryFileView(
                    f.Path.Value,
                    f.Versions,
                    f.Copies.OrderBy(c => c.MachineId).ThenBy(c => c.Folder, StringComparer.Ordinal)
                        .Select(c => new MemoryCopyView(c.MachineId, machineNames.GetValueOrDefault(c.MachineId), c.Folder, c.VersionNumber, c.DeletedAt, c.UpdatedAt))
                        .ToList(),
                    f.UpdatedAt))
                .ToList());
    }
    /// <summary>The content of one of the owner's projects' memory files: each file's current version, or one given version of one file.</summary>
    /// <remarks>
    /// <c>path</c> limits the answer to one file, and <c>version</c> (which needs <c>path</c>) asks for that version
    /// instead of the current one. The current version is <see cref="MemoryFile.Current"/>. Copies are loaded to choose it, and then only the
    /// chosen versions' content, so a file with a long history costs one version's bytes.
    /// </remarks>
    public async Task<MemoryResult<MemoryProjectContent>> GetContentAsync(Guid ownerId, Guid projectId, string? path, int? version, CancellationToken cancellationToken)
    {
        RelativePath? wanted = null;
        if (path is not null)
        {
            if (path.Length > MemoryLimits.MaxPathLength || !RelativePath.TryCreate(path, out var valid))
                return MemoryResult<MemoryProjectContent>.Invalid($"'{path}' is not a valid relative path of at most {MemoryLimits.MaxPathLength} characters.");
            wanted = valid;
        }
        else if (version is not null)
        {
            return MemoryResult<MemoryProjectContent>.Invalid("A version needs the path of the file it is a version of.");
        }

        var name = await _db.Projects.AsNoTracking()
            .Where(p => p.OwnerId == ownerId && p.Id == projectId)
            .Select(p => p.Name)
            .SingleOrDefaultAsync(cancellationToken);
        if (name is null)
            return MemoryResult<MemoryProjectContent>.UnknownProject($"No project {projectId}.");

        var query = _db.MemoryFiles.AsNoTracking().Include(f => f.Copies).Where(f => f.OwnerId == ownerId && f.ProjectId == projectId);
        if (wanted is not null)
            query = query.Where(f => f.Path == wanted);
        var files = await query.ToListAsync(cancellationToken);
        if (wanted is not null && files.Count == 0)
            return MemoryResult<MemoryProjectContent>.UnknownFile($"Project {projectId} has no memory file {wanted.Value}.");

        var chosen = files.Select(f => (File: f, Current: f.Current())).Select(c => (c.File, c.Current, Number: version ?? c.Current.Version)).ToList();
        var ids = chosen.Select(c => c.File.Id).ToList();
        var numbers = chosen.Select(c => c.Number).Distinct().ToList();
        // Can fetch a few versions too many (another file's number); the exact pairs are matched below.
        var versions = (await _db.MemoryFileVersions.AsNoTracking()
                .Where(v => ids.Contains(v.MemoryFileId) && numbers.Contains(v.Number))
                .ToListAsync(cancellationToken))
            .ToDictionary(v => (v.MemoryFileId, v.Number));
        if (version is not null && !versions.ContainsKey((chosen[0].File.Id, version.Value)))
            return MemoryResult<MemoryProjectContent>.UnknownFile($"{wanted!.Value} has no version {version}.");

        var machineIds = files.SelectMany(f => f.Copies).Where(c => c.DeletedAt is not null).Select(c => c.MachineId).Distinct().ToList();
        var machineNames = await _db.Machines.AsNoTracking()
            .Where(m => m.OwnerId == ownerId && machineIds.Contains(m.Id))
            .ToDictionaryAsync(m => m.Id, m => m.Name, cancellationToken);

        return MemoryResult<MemoryProjectContent>.Ok(new MemoryProjectContent(
            projectId,
            name,
            chosen.OrderBy(c => c.File.Path.Value, StringComparer.Ordinal)
                .Select(c =>
                {
                    var v = versions[(c.File.Id, c.Number)];
                    return new MemoryFileContent(
                        c.File.Path.Value, v.Number, v.Sha256.Value, v.Size, v.Content, v.Number == c.Current.Version, c.Current.DeletedEverywhere,
                        c.File.Copies.Where(k => k.DeletedAt is not null)
                            .OrderBy(k => k.MachineId).ThenBy(k => k.Folder, StringComparer.Ordinal)
                            .Select(k => new MemoryCopyView(k.MachineId, machineNames.GetValueOrDefault(k.MachineId), k.Folder, k.VersionNumber, k.DeletedAt, k.UpdatedAt))
                            .ToList());
                })
                .ToList()));
    }
}

/// <summary>A memory push: the changed and deleted files of one project folder on one machine.</summary>
/// <param name="MachineId">The registered machine the folder is on.</param>
/// <param name="Project">The facts of the folder the memory belongs to, as for sessions.</param>
/// <param name="Files">Files whose content changed or is new, with their content.</param>
/// <param name="Deleted">Paths of files deleted in the folder since they were backed up.</param>
public sealed record MemoryPushRequest(Guid MachineId, ProjectFacts? Project, IReadOnlyList<MemoryFileUpload>? Files, IReadOnlyList<string>? Deleted);

/// <summary>One file of a memory push.</summary>
/// <param name="Path">Its path within the memory folder, with <c>/</c> separators.</param>
/// <param name="Content">Its bytes (base64 in JSON).</param>
public sealed record MemoryFileUpload(string? Path, byte[]? Content);

/// <summary>What a push did: the project and folder the memory was filed under, and each file's outcome.</summary>
public sealed record MemoryPushResult(Guid ProjectId, string ProjectName, string Folder, IReadOnlyList<MemoryFileOutcome> Files);

/// <summary>What happened to one file of a push.</summary>
/// <param name="Path">The file's path.</param>
/// <param name="Change">What changed.</param>
/// <param name="Version">The version this machine's copy now points at; <c>null</c> for a deletion of a file it never backed up.</param>
public sealed record MemoryFileOutcome(string Path, MemoryFileChange Change, int? Version);

/// <summary>A project that has memory.</summary>
public sealed record MemoryProjectSummary(Guid ProjectId, string ProjectName, int Files, int Versions, int Machines, DateTimeOffset UpdatedAt);

/// <summary>A project's memory files.</summary>
public sealed record MemoryProjectFiles(Guid ProjectId, string ProjectName, IReadOnlyList<MemoryFileView> Files);

/// <summary>One memory file, its versions and its copies, without content.</summary>
public sealed record MemoryFileView(string Path, IReadOnlyList<MemoryVersionView> Versions, IReadOnlyList<MemoryCopyView> Copies, DateTimeOffset UpdatedAt);

/// <summary>One version of a memory file.</summary>
/// <param name="Version">Its number, from 1.</param>
/// <param name="Sha256">Uppercase hex SHA-256 of its content.</param>
/// <param name="Size">Its size in bytes.</param>
/// <param name="MachineId">The machine that first sent this content.</param>
/// <param name="CreatedAt">When the registry first stored it.</param>
public sealed record MemoryVersionView(int Version, string Sha256, long Size, Guid MachineId, DateTimeOffset CreatedAt);

/// <summary>One checkout's copy of a memory file.</summary>
/// <param name="MachineId">The machine.</param>
/// <param name="MachineName">The machine's name, or <c>null</c> once it is no longer registered.</param>
/// <param name="Folder">The folder on that machine: the main worktree for git.</param>
/// <param name="Version">The version it last sent.</param>
/// <param name="DeletedAt">When the file was found deleted there; <c>null</c> while present.</param>
/// <param name="UpdatedAt">When this copy last changed.</param>
public sealed record MemoryCopyView(Guid MachineId, string? MachineName, string Folder, int Version, DateTimeOffset? DeletedAt, DateTimeOffset UpdatedAt);

/// <summary>The content of a project's memory files.</summary>
public sealed record MemoryProjectContent(Guid ProjectId, string ProjectName, IReadOnlyList<MemoryFileContent> Files);

/// <summary>One version of one memory file, with its bytes.</summary>
/// <param name="Path">The file's path.</param>
/// <param name="Version">The version's number.</param>
/// <param name="Sha256">Uppercase hex SHA-256 of <paramref name="Content"/>.</param>
/// <param name="Size">Its size in bytes.</param>
/// <param name="Content">Its bytes (base64 in JSON).</param>
/// <param name="Current">Whether this is the file's current version (<see cref="MemoryFile.Current"/>).</param>
/// <param name="DeletedEverywhere">Whether every machine's copy has deleted the file.</param>
/// <param name="DeletedOn">The copies that deleted it; the others still hold it.</param>
public sealed record MemoryFileContent(
    string Path, int Version, string Sha256, long Size, byte[] Content, bool Current, bool DeletedEverywhere, IReadOnlyList<MemoryCopyView> DeletedOn);

/// <summary>Why a memory request was refused.</summary>
public enum MemoryFailure
{
    None,
    Invalid,
    TooLarge,
    UnknownMachine,
    UnknownProject,
    UnknownFile,
}

/// <summary>A value, or why a memory request was refused.</summary>
public sealed record MemoryResult<T>(T? Value, MemoryFailure Failure, string? Message)
{
    public static MemoryResult<T> Ok(T value) => new(value, MemoryFailure.None, null);

    public static MemoryResult<T> Invalid(string message) => new(default, MemoryFailure.Invalid, message);

    public static MemoryResult<T> TooLarge(string message) => new(default, MemoryFailure.TooLarge, message);

    public static MemoryResult<T> UnknownMachine(string message) => new(default, MemoryFailure.UnknownMachine, message);

    public static MemoryResult<T> UnknownProject(string message) => new(default, MemoryFailure.UnknownProject, message);

    public static MemoryResult<T> UnknownFile(string message) => new(default, MemoryFailure.UnknownFile, message);
}
