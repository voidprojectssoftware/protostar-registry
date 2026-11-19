using System.Text.Json;
using System.Text.RegularExpressions;

namespace Protostar.Registry.Api.Projects;

/// <summary>What a client found out about a working directory: the raw facts a project is resolved from.</summary>
/// <remarks>
/// <para>
/// Only the client can run git in the checkout, so it gathers these and the registry decides what they mean
/// (ADR 0002). They are kept as sent, so the mapping to projects can be recomputed when the rules change
/// without asking the client again.
/// </para>
/// <para>
/// <see cref="RootCommit"/> is the first-parent root commit of the default branch; a client sends none for a
/// shallow clone, where git reports the shallow boundary as the root. <see cref="Remotes"/> are raw URLs; the
/// registry normalises them (<see cref="RemoteUrl"/>).
/// </para>
/// </remarks>
public sealed partial record ProjectFacts
{
    private const int MaxPath = 2000;

    /// <summary>The working directory the facts are about, as recorded by the harness.</summary>
    public string WorkingDirectory { get; init; } = "";

    /// <summary>The nearest existing folder at or above <see cref="WorkingDirectory"/>, where git was run.</summary>
    public string Directory { get; init; } = "";

    /// <summary>Whether <see cref="Directory"/> is inside a git work tree.</summary>
    public bool IsGit { get; init; }

    /// <summary>Whether the repository is a shallow clone.</summary>
    public bool Shallow { get; init; }

    /// <summary>The first-parent root commit of the default branch, as a full hex hash; <c>null</c> when unknown or shallow.</summary>
    public string? RootCommit { get; init; }

    /// <summary>Every root commit reachable from <c>HEAD</c>. Recorded, never matched on directly.</summary>
    public IReadOnlyList<string> Roots { get; init; } = [];

    /// <summary>The branch <see cref="RootCommit"/> was computed from, for example <c>refs/remotes/origin/HEAD</c>.</summary>
    public string? DefaultBranch { get; init; }

    /// <summary>The repository's remotes, as configured.</summary>
    public IReadOnlyList<GitRemote> Remotes { get; init; } = [];

    /// <summary>The work tree's top level (<c>git rev-parse --show-toplevel</c>).</summary>
    public string? TopLevel { get; init; }

    /// <summary>The main worktree's folder: the parent of the shared git directory. The same for every worktree of a repo.</summary>
    public string? MainWorktree { get; init; }

    /// <summary>The path of <see cref="Directory"/> within the repo (<c>git rev-parse --show-prefix</c>), empty at the top.</summary>
    public string? Subpath { get; init; }

    /// <summary>The root commit to identify the repo by: none for a shallow clone, whatever the client sent.</summary>
    public string? TrustedRootCommit => IsGit && !Shallow ? RootCommit : null;

    [GeneratedRegex("^([0-9a-f]{40}|[0-9a-f]{64})$")]
    private static partial Regex CommitPattern();

    /// <summary>Why these facts cannot be used, or <c>null</c> when they can.</summary>
    public string? Problem()
    {
        if (string.IsNullOrWhiteSpace(WorkingDirectory) || WorkingDirectory.Length > MaxPath)
            return "project.workingDirectory is required and may be at most 2000 characters.";
        if (string.IsNullOrWhiteSpace(Directory) || Directory.Length > MaxPath)
            return "project.directory is required and may be at most 2000 characters.";
        if (RootCommit is not null && !CommitPattern().IsMatch(RootCommit))
            return "project.rootCommit must be a full lowercase hex commit hash.";
        if (Roots.Count > 100 || Roots.Any(r => !CommitPattern().IsMatch(r)))
            return "project.roots must be at most 100 full lowercase hex commit hashes.";
        if (Remotes.Count > 20 || Remotes.Any(r => string.IsNullOrWhiteSpace(r.Name) || r.Name.Length > 200 || string.IsNullOrWhiteSpace(r.Url) || r.Url.Length > MaxPath))
            return "project.remotes must be at most 20, each with a name and a URL.";
        if (new[] { TopLevel, MainWorktree, Subpath, DefaultBranch }.Any(p => p?.Length > MaxPath))
            return "project paths may be at most 2000 characters.";
        return null;
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The facts as stored beside a transcript.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>Reads facts stored by <see cref="ToJson"/>.</summary>
    public static ProjectFacts FromJson(string json) => JsonSerializer.Deserialize<ProjectFacts>(json, Json)!;
}

/// <summary>One git remote as configured in a checkout.</summary>
/// <param name="Name">The remote's name, for example <c>origin</c>.</param>
/// <param name="Url">Its fetch URL, unnormalised.</param>
public sealed record GitRemote(string Name, string Url);
