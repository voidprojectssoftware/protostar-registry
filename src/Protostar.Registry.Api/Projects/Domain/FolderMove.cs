namespace Protostar.Registry.Api.Projects;

/// <summary>Something keyed by project that goes with its folder when the folder is linked or unlinked: a project skill or agent.</summary>
/// <remarks>Implemented by the aggregates <see cref="FolderMove"/> moves; each module keeps its own table and its own clash check.</remarks>
public interface IMovesWithFolder
{
    /// <summary>The creator it is namespaced to.</summary>
    Guid CreatorId { get; }

    /// <summary>Its name, unique per creator and project within its own module.</summary>
    string Name { get; }

    /// <summary>The project it belongs to; <c>null</c> when global, which never moves.</summary>
    Guid? ProjectId { get; }

    /// <summary>The folder whose move carried it into a project other folders share; <c>null</c> otherwise.</summary>
    string? CarriedByFolderKey { get; }

    /// <summary>Moves it to <paramref name="projectId"/> because its folder moved there.</summary>
    /// <param name="projectId">The project the folder now belongs to.</param>
    /// <param name="carriedByFolderKey">The folder that carried it, when the project is shared; <c>null</c> when the project is the folder's alone.</param>
    void MoveWithFolder(Guid projectId, string? carriedByFolderKey);
}

/// <summary>The rule for which project skills and agents go with a folder when it is linked or unlinked (registry#39, ADR 0003).</summary>
/// <remarks>
/// <para>
/// Skills and agents are keyed by project, not by folder, so "the folder's skills" is decided like this. When a folder
/// leaves a project that is its alone (<see cref="Project.StandsOnlyFor"/>: a folder outside git, or a fork kept
/// apart), everything of that project goes with it. When it leaves a project other folders share (a git repo,
/// or a project it was linked into), only what its own earlier move carried in goes with it
/// (<see cref="IMovesWithFolder.CarriedByFolderKey"/>); the rest belongs to the other folders. That is what makes link then
/// unlink, and the fork toggle, a round trip.
/// </para>
/// <para>
/// Something arriving in a shared project remembers the folder that carried it; arriving in a project that is
/// the folder's alone, it forgets it, since everything there follows the folder anyway.
/// </para>
/// <para>
/// A version history is never merged into another: when the destination already has one of the same name
/// (and creator) in the same module, the caller refuses the whole move (<see cref="Clashes"/>). A skill and an agent may
/// share a name, so the caller checks each module against its own table.
/// </para>
/// </remarks>
public static class FolderMove
{
    /// <summary>The items of <paramref name="from"/> that go with the folder <paramref name="folderKey"/> when it leaves.</summary>
    /// <param name="from">The project the folder is leaving, with its keys as they are before the move.</param>
    /// <param name="itemsOfFrom">Every skill (or every agent) of <paramref name="from"/>.</param>
    /// <param name="folderKey">The folder, <c>&lt;machine id&gt;:&lt;path&gt;</c>.</param>
    public static IReadOnlyList<T> Following<T>(Project from, IEnumerable<T> itemsOfFrom, string folderKey)
        where T : IMovesWithFolder
    {
        var all = from.StandsOnlyFor(folderKey);
        return itemsOfFrom.Where(s => s.ProjectId == from.Id && (all || s.CarriedByFolderKey == folderKey)).ToList();
    }

    /// <summary>The names among <paramref name="arriving"/> that the destination already has for the same creator, sorted.</summary>
    /// <remarks>Exact names, as the (creator, project, name) unique indexes compare them. Pass one module's items and names at a time.</remarks>
    public static IReadOnlyList<string> Clashes<T>(IEnumerable<T> arriving, IEnumerable<(Guid CreatorId, string Name)> present)
        where T : IMovesWithFolder
    {
        var taken = present.ToHashSet();
        return arriving.Where(s => taken.Contains((s.CreatorId, s.Name))).Select(s => s.Name).Order(StringComparer.Ordinal).ToList();
    }

    /// <summary>Moves <paramref name="following"/> into <paramref name="to"/>, the project the folder now belongs to.</summary>
    /// <param name="following">What <see cref="Following"/> chose; the caller has checked <see cref="Clashes"/>.</param>
    /// <param name="to">The destination, with its keys as they are after the move.</param>
    /// <param name="folderKey">The folder.</param>
    public static void Move<T>(IEnumerable<T> following, Project to, string folderKey)
        where T : IMovesWithFolder
    {
        var carriedBy = to.StandsOnlyFor(folderKey) ? null : folderKey;
        foreach (var item in following)
            item.MoveWithFolder(to.Id, carriedBy);
    }
}
