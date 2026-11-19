using Protostar.Registry.Api.Projects;
using Xunit;

namespace Protostar.Registry.Tests.Projects.Domain;

/// <summary>Contract tests for <see cref="ProjectIdentity"/> and <see cref="ProjectMatcher"/>: ADR 0002's awkward cases.</summary>
public sealed class ProjectResolutionTests
{
    private const string Root = "59dc947b0dba097d8d1dca3121154d3839dc3983";
    private const string OtherRoot = "4bfc16bf011700000000000000000000000000aa";
    private static readonly Guid Linux = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Windows = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly DateTimeOffset Now = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

    private static ProjectFacts Git(string directory, string mainWorktree, string? root = Root, bool shallow = false, string? subpath = null, params GitRemote[] remotes) => new()
    {
        WorkingDirectory = directory,
        Directory = directory,
        IsGit = true,
        Shallow = shallow,
        RootCommit = root,
        Roots = root is null ? [] : [root],
        Remotes = remotes,
        TopLevel = directory,
        MainWorktree = mainWorktree,
        Subpath = subpath,
    };

    private static ProjectFacts Plain(string directory) => new() { WorkingDirectory = directory, Directory = directory };

    private static readonly GitRemote Origin = new("origin", "git@github.com:acme/widget.git");

    private static List<ProjectKey> Keys(Project project) => [.. project.Keys];

    [Fact]
    public void The_same_repo_on_two_machines_and_paths_has_the_same_root_key()
    {
        var linux = ProjectIdentity.From(Linux, Git("/home/me/src/widget", "/home/me/src/widget", remotes: Origin));
        var windows = ProjectIdentity.From(Windows, Git(@"C:\src\Widget", @"C:\src\Widget", remotes: new GitRemote("origin", "https://github.com/acme/widget")));

        Assert.Equal(linux.RootCommit, windows.RootCommit);
        Assert.Equal("github.com/acme/widget", linux.Name);
        Assert.Equal(linux.Name, windows.Name);
    }

    [Fact]
    public void A_worktree_is_filed_by_its_main_worktree_folder()
    {
        var identity = ProjectIdentity.From(Linux, Git("/home/me/src/widget-56", "/home/me/src/widget"));

        Assert.Equal("/home/me/src/widget", identity.Folder);
        Assert.Equal($"{Linux}:/home/me/src/widget", identity.FolderKey);
    }

    [Fact]
    public void A_shallow_clone_never_trusts_its_root_and_is_matched_by_remote()
    {
        var full = Project.Create(Guid.NewGuid(), "github.com/acme/widget", Now);
        full.Attach(ProjectKeyKind.RootCommit, Root, Now);
        full.Attach(ProjectKeyKind.Remote, "github.com/acme/widget", Now);

        var shallow = ProjectIdentity.From(Linux, Git("/tmp/shallow", "/tmp/shallow", root: "e9f7ed1ea4f9eb2f761e7cf7a90f8baba61c0588", shallow: true, remotes: new GitRemote("origin", "https://github.com/acme/widget.git")));
        var match = ProjectMatcher.Match(shallow, Keys(full), []);

        Assert.Null(shallow.RootCommit);
        Assert.Equal(new ProjectMatch(full.Id, ProjectRecognition.Remote), match);
        Assert.Empty(ProjectMatcher.KeysToAttach(shallow, match.RecognisedBy));
    }

    [Fact]
    public void A_shallow_clone_with_an_unknown_remote_is_filed_by_its_folder()
    {
        var shallow = ProjectIdentity.From(Linux, Git("/tmp/shallow", "/tmp/shallow", root: null, shallow: true, remotes: Origin));

        var match = ProjectMatcher.Match(shallow, [], []);

        Assert.Equal(ProjectRecognition.New, match.RecognisedBy);
        Assert.Equal([(ProjectKeyKind.Folder, shallow.FolderKey)], ProjectMatcher.KeysToAttach(shallow, match.RecognisedBy));
    }

    [Fact]
    public void A_full_clone_attaches_its_root_and_remotes_so_shallow_clones_can_follow()
    {
        var identity = ProjectIdentity.From(Linux, Git("/src/w", "/src/w", remotes: [Origin, new GitRemote("upstream", "https://github.com/upstream/widget.git")]));

        var keys = ProjectMatcher.KeysToAttach(identity, ProjectRecognition.New);

        Assert.Equal(
            [(ProjectKeyKind.RootCommit, Root), (ProjectKeyKind.Remote, "github.com/acme/widget"), (ProjectKeyKind.Remote, "github.com/upstream/widget")],
            keys);
    }

    [Fact]
    public void Origin_names_the_project_even_when_listed_after_another_remote()
    {
        var identity = ProjectIdentity.From(Linux, Git("/src/w", "/src/w", remotes: [new GitRemote("upstream", "https://github.com/upstream/widget.git"), Origin]));

        Assert.Equal("github.com/acme/widget", identity.Name);
    }

    [Fact]
    public void A_repo_with_no_remote_is_named_by_its_main_worktree_folder()
    {
        var identity = ProjectIdentity.From(Linux, Git("/src/widget-wt", "/src/widget"));

        Assert.Equal("widget", identity.Name);
    }

    [Fact]
    public void A_plain_folder_is_its_own_project_per_machine_and_path()
    {
        var here = ProjectIdentity.From(Linux, Plain("/home/me/notes"));
        var there = ProjectIdentity.From(Windows, Plain("/home/me/notes"));

        Assert.Null(here.RootCommit);
        Assert.NotEqual(here.FolderKey, there.FolderKey);
        Assert.Equal("notes", here.Name);
        Assert.Equal(ProjectRecognition.New, ProjectMatcher.Match(here, [], []).RecognisedBy);
    }

    [Fact]
    public void A_plain_folder_seen_before_matches_its_folder_project()
    {
        var identity = ProjectIdentity.From(Linux, Plain("/home/me/notes"));
        var project = Project.Create(Guid.NewGuid(), identity.Name, Now);
        project.Attach(ProjectKeyKind.Folder, identity.FolderKey, Now);

        Assert.Equal(new ProjectMatch(project.Id, ProjectRecognition.Folder), ProjectMatcher.Match(identity, Keys(project), []));
    }

    [Fact]
    public void A_path_known_only_from_transcript_lines_resolves_like_a_plain_folder()
    {
        Assert.Equal(
            ProjectIdentity.From(Linux, Plain("/home/me/notes")).FolderKey,
            ProjectIdentity.FromFolder(Linux, "/home/me/notes").FolderKey);
    }

    [Fact]
    public void A_git_checkout_never_falls_back_to_a_folder_project()
    {
        var identity = ProjectIdentity.From(Linux, Git("/src/w", "/src/w"));
        var folderProject = Project.Create(Guid.NewGuid(), "w", Now);
        folderProject.Attach(ProjectKeyKind.Folder, identity.FolderKey, Now);

        Assert.Equal(ProjectRecognition.New, ProjectMatcher.Match(identity, Keys(folderProject), []).RecognisedBy);
    }

    [Fact]
    public void Only_the_first_parent_root_is_matched_not_any_recorded_root()
    {
        // A repo that pulled a library in with git subtree has the library's root among its roots.
        var library = Project.Create(Guid.NewGuid(), "lib", Now);
        library.Attach(ProjectKeyKind.RootCommit, OtherRoot, Now);
        var app = ProjectIdentity.From(Linux, Git("/src/app", "/src/app") with { Roots = [Root, OtherRoot] });

        Assert.Equal(ProjectRecognition.New, ProjectMatcher.Match(app, Keys(library), []).RecognisedBy);
    }

    [Fact]
    public void A_checkout_whose_first_parent_root_moved_stays_on_its_project()
    {
        var project = Project.Create(Guid.NewGuid(), "w", Now);
        project.Attach(ProjectKeyKind.RootCommit, OtherRoot, Now);
        var moved = ProjectIdentity.From(Linux, Git("/src/w", "/src/w") with { Roots = [Root, OtherRoot] });

        var match = ProjectMatcher.Match(moved, Keys(project), [project.Id]);

        Assert.Equal(new ProjectMatch(project.Id, ProjectRecognition.PreviousRootCommit), match);
        Assert.Contains((ProjectKeyKind.RootCommit, Root), ProjectMatcher.KeysToAttach(moved, match.RecognisedBy));
    }

    [Fact]
    public void Only_a_repo_with_a_remote_renames_its_project()
    {
        var withRemote = ProjectIdentity.From(Linux, Git("/src/w", "/src/w", remotes: Origin));
        var withoutRemote = ProjectIdentity.From(Linux, Git("/src/w", "/src/w"));

        Assert.True(ProjectMatcher.Renames(withRemote, ProjectRecognition.RootCommit));
        Assert.False(ProjectMatcher.Renames(withoutRemote, ProjectRecognition.RootCommit));
        Assert.False(ProjectMatcher.Renames(withRemote, ProjectRecognition.Folder));
    }

    [Fact]
    public void A_subfolder_records_its_path_within_the_repo()
    {
        var identity = ProjectIdentity.From(Linux, Git("/src/w/src", "/src/w", subpath: "src/"));

        Assert.Equal("src", identity.Subpath);
        Assert.Equal("/src/w", identity.Folder);
    }
}
