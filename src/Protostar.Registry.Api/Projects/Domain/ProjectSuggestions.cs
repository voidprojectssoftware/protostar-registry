namespace Protostar.Registry.Api.Projects;

/// <summary>A project a folder might belong to, offered to the user and never applied without them.</summary>
/// <param name="ProjectId">The suggested project.</param>
/// <param name="Name">Its display name.</param>
/// <param name="Reason">Why it looks like a match, for the user to judge.</param>
public sealed record ProjectSuggestion(Guid ProjectId, string Name, string Reason);

/// <summary>Finds projects a folder outside git looks like it belongs to.</summary>
/// <remarks>
/// Owner decision 2 in ADR 0002 (registry#29): folders that are not git repos are never linked automatically,
/// because a wrong merge mixes memory between unrelated folders. They are suggested, and the user links them.
/// A folder looks like a project when the last segment of its path matches a folder the project already has
/// (on another machine or at another path), or the last segment of the project's name, ignoring case. So the
/// <c>protostar</c> workspace folder on Windows and on Linux suggest each other, and a plain <c>widget</c>
/// folder suggests <c>github.com/acme/widget</c>.
/// </remarks>
public static class ProjectSuggestions
{
    /// <summary>Suggestions for <paramref name="identity"/>, given the owner's projects and the one it resolves to now.</summary>
    /// <param name="identity">The folder's identity.</param>
    /// <param name="recognisedBy">How it resolved; a git checkout or a linked folder gets no suggestions.</param>
    /// <param name="current">The project it resolves to now, never suggested; <c>null</c> when none exists yet.</param>
    /// <param name="projects">The owner's projects, with their keys.</param>
    public static IReadOnlyList<ProjectSuggestion> For(
        ProjectIdentity identity, ProjectRecognition recognisedBy, Guid? current, IEnumerable<Project> projects)
    {
        if (identity.RootCommit is not null || identity.Shallow || recognisedBy == ProjectRecognition.Link)
            return [];

        var name = ProjectIdentity.LastSegmentOf(identity.Folder);
        var suggestions = new List<ProjectSuggestion>();
        foreach (var project in projects.Where(p => p.Id != current))
        {
            var folder = project.Keys
                .Where(k => k.Kind is ProjectKeyKind.Folder or ProjectKeyKind.Link && k.Key != identity.FolderKey)
                .Select(k => ProjectIdentity.TryParseFolderKey(k.Key, out _, out var path) ? path : null)
                .FirstOrDefault(path => path is not null && Same(ProjectIdentity.LastSegmentOf(path), name));

            if (folder is not null)
                suggestions.Add(new ProjectSuggestion(project.Id, project.Name, $"it has a folder named '{name}' elsewhere: {folder}"));
            else if (Same(ProjectIdentity.LastSegmentOf(project.Name), name))
                suggestions.Add(new ProjectSuggestion(project.Id, project.Name, $"its name ends in '{name}'"));
        }

        return suggestions;
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
