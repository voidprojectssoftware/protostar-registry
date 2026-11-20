using Protostar.Registry.Api.Skills;

namespace Protostar.Registry.Api.Sessions;

/// <summary>A skill a session used, matched against the owner's skills in the registry.</summary>
/// <param name="Name">The name as invoked; a plugin skill keeps its <c>plugin:skill</c> form.</param>
/// <param name="Uses">How many times it was invoked.</param>
/// <param name="FirstUsedAt">The earliest invocation's timestamp, when one was recorded.</param>
/// <param name="LastUsedAt">The latest invocation's timestamp, when one was recorded.</param>
/// <param name="SkillId">The registry skill the name matched; <c>null</c> when it matched none, or the skill was deleted.</param>
/// <param name="Version">The version inferred to be the one used; <c>null</c> when no version had been pushed by then (allowing <see cref="SessionSkills.PushGrace"/>).</param>
/// <param name="Deleted">True when the name was a registry skill that has since been deleted.</param>
public sealed record SessionSkill(
    string Name, int Uses, DateTimeOffset? FirstUsedAt, DateTimeOffset? LastUsedAt, Guid? SkillId, int? Version, bool Deleted);

/// <summary>Matches the skills a session invoked to the registry skills they were, and infers which version was used.</summary>
/// <remarks>
/// <para>
/// A transcript records only the name the agent invoked, not the skill's content, so a skill is matched by name and its
/// version is inferred, never known: it is the newest version created at or before the session's last invocation of it
/// plus <see cref="PushGrace"/> (the one in force the last time it was used). With no such version (the skill was first
/// pushed after the session, or no invocation had a timestamp) the skill is reported with no version.
/// </para>
/// <para>
/// The grace exists because the CLI backs a skill up after it is used, not before: its capture hook runs on Claude
/// Code's <c>PostToolUse</c> event for the <c>Skill</c> tool, so a skill edited and then used is pushed as a new version
/// a few seconds after the <c>tool_use</c> line that records the use. Without the grace that common case reports the
/// version from before the edit, and a skill used for the first time reports none.
/// </para>
/// <para>
/// A name is looked up among the owner's personal (global) skills first, then in the session's project, and the first
/// scope that knows the name decides: a personal skill wins over a project skill of the same name. That is Claude Code's
/// loading rule (enterprise, then personal, then project), so it is the skill the session actually ran. Within a scope a live
/// skill of that name matches; otherwise a removed name does: one renamed away matches the skill under its new name
/// (renaming keeps the skill and its versions), and one deleted is reported as deleted, with no skill or version, since
/// both went with it. A plugin skill (<c>plugin:skill</c>) never matches: plugins' skills are not pushed to the registry.
/// </para>
/// <para>
/// Matching happens when the skills are read, not when the transcript is parsed, so a skill pushed, renamed or deleted
/// later, or a session filed under its project later, is always reflected.
/// </para>
/// </remarks>
public static class SessionSkills
{
    /// <summary>How long after a skill's last use a pushed version still counts as the one that was used.</summary>
    /// <remarks>
    /// The capture hook pushes on <c>PostToolUse</c>, unthrottled (only the <c>Stop</c> transcript backup is throttled),
    /// and gives up after 15 seconds; process start adds about a second. The use is stamped by the client's clock and
    /// the version by the registry's, so the window also absorbs a modest skew between them. Two minutes covers both
    /// with room to spare while staying far shorter than the gap before a deliberate later edit is pushed, which is
    /// the error the other way: a version pushed within the grace by other means (a manual push, another machine) is
    /// taken as the one used. Versions do not record the machine that pushed them, so the grace cannot be narrowed to
    /// the session's machine.
    /// </remarks>
    public static readonly TimeSpan PushGrace = TimeSpan.FromMinutes(2);

    /// <summary>Matches each of <paramref name="uses"/>, returned earliest first used first, then by name.</summary>
    /// <param name="uses">The skills the session invoked.</param>
    /// <param name="projectId">The session's project; <c>null</c> when it is not filed under one.</param>
    /// <param name="skills">The owner's skills that may match, with their versions loaded.</param>
    /// <param name="tombstones">The owner's removed skill names that may match.</param>
    public static IReadOnlyList<SessionSkill> Match(
        IReadOnlyList<SkillUse> uses, Guid? projectId, IReadOnlyList<Skill> skills, IReadOnlyList<SkillTombstone> tombstones)
    {
        Guid?[] scopes = projectId is { } project ? [null, project] : [null];
        return uses
            .Select(use => Match(use, scopes, skills, tombstones))
            .OrderBy(s => s.FirstUsedAt ?? DateTimeOffset.MaxValue)
            .ThenBy(s => s.Name, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>True when <paramref name="name"/> is a plugin's skill, which no registry skill can be.</summary>
    public static bool IsPluginSkill(string name) => name.Contains(':');

    private static SessionSkill Match(SkillUse use, Guid?[] scopes, IReadOnlyList<Skill> skills, IReadOnlyList<SkillTombstone> tombstones)
    {
        var unmatched = new SessionSkill(use.Name, use.Uses, use.FirstUsedAt, use.LastUsedAt, null, null, false);
        if (IsPluginSkill(use.Name))
            return unmatched;

        foreach (var scope in scopes)
        {
            if (Find(skills, scope, use.Name) is { } live)
                return Matched(unmatched, live);

            if (tombstones.FirstOrDefault(t => t.ProjectId == scope && t.Name == use.Name) is { } removed)
            {
                return removed.RenamedTo is { } renamedTo && Find(skills, scope, renamedTo) is { } renamed
                    ? Matched(unmatched, renamed)
                    : unmatched with { Deleted = true };
            }
        }

        return unmatched;
    }

    private static Skill? Find(IReadOnlyList<Skill> skills, Guid? scope, string name) =>
        skills.FirstOrDefault(s => s.ProjectId == scope && s.Name == name);

    private static SessionSkill Matched(SessionSkill use, Skill skill)
    {
        var version = use.LastUsedAt is { } at
            ? skill.Versions.Where(v => v.CreatedAt <= at + PushGrace).MaxBy(v => v.VersionNumber)
            : null;
        return use with { SkillId = skill.Id, Version = version?.VersionNumber };
    }
}
