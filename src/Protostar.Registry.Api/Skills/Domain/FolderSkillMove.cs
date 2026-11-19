using Protostar.Registry.Api.Projects;

namespace Protostar.Registry.Api.Skills;

/// <summary>The rule for which project skills go with a folder when it is linked or unlinked (registry#39).</summary>
/// <remarks>
/// <para>
/// Skills are keyed by project, not by folder, so "the folder's skills" is decided like this. When a folder
/// leaves a project that is its alone (<see cref="Project.StandsOnlyFor"/>: a folder outside git, or a fork kept
/// apart), every skill of that project goes with it. When it leaves a project other folders share (a git repo,
/// or a project it was linked into), only the skills its own earlier move carried in go with it
/// (<see cref="Skill.CarriedByFolderKey"/>); the rest belong to the other folders. That is what makes link then
/// unlink, and the fork toggle, a round trip.
/// </para>
/// <para>
/// A skill arriving in a shared project remembers the folder that carried it; one arriving in a project that is
/// the folder's alone forgets it, since everything there follows the folder anyway.
/// </para>
/// <para>
/// A version history is never merged into another: when the destination already has a skill of the same name
/// (and creator), the caller refuses the whole move (<see cref="Clashes"/>).
/// </para>
/// </remarks>
public static class FolderSkillMove
{
    /// <summary>The skills of <paramref name="from"/> that go with the folder <paramref name="folderKey"/> when it leaves.</summary>
    /// <param name="from">The project the folder is leaving, with its keys as they are before the move.</param>
    /// <param name="skillsOfFrom">Every skill of <paramref name="from"/>.</param>
    /// <param name="folderKey">The folder, <c>&lt;machine id&gt;:&lt;path&gt;</c>.</param>
    public static IReadOnlyList<Skill> Following(Project from, IEnumerable<Skill> skillsOfFrom, string folderKey)
    {
        var all = from.StandsOnlyFor(folderKey);
        return skillsOfFrom.Where(s => s.ProjectId == from.Id && (all || s.CarriedByFolderKey == folderKey)).ToList();
    }

    /// <summary>The names among <paramref name="arriving"/> that the destination already has for the same creator, sorted.</summary>
    /// <remarks>Exact names, as the (creator, project, name) unique index compares them.</remarks>
    public static IReadOnlyList<string> Clashes(IEnumerable<Skill> arriving, IEnumerable<(Guid CreatorId, string Name)> present)
    {
        var taken = present.ToHashSet();
        return arriving.Where(s => taken.Contains((s.CreatorId, s.Name))).Select(s => s.Name).Order(StringComparer.Ordinal).ToList();
    }

    /// <summary>Moves <paramref name="following"/> into <paramref name="to"/>, the project the folder now belongs to.</summary>
    /// <param name="following">What <see cref="Following"/> chose; the caller has checked <see cref="Clashes"/>.</param>
    /// <param name="to">The destination, with its keys as they are after the move.</param>
    /// <param name="folderKey">The folder.</param>
    public static void Move(IEnumerable<Skill> following, Project to, string folderKey)
    {
        var carriedBy = to.StandsOnlyFor(folderKey) ? null : folderKey;
        foreach (var skill in following)
            skill.MoveWithFolder(to.Id, carriedBy);
    }
}
