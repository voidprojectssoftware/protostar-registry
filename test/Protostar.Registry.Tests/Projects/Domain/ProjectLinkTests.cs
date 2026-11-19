using Protostar.Registry.Api.Projects;
using Xunit;

namespace Protostar.Registry.Tests.Projects.Domain;

/// <summary>Contract tests for folder links and suggestions (protostar-cli#57, ADR 0002 owner decisions).</summary>
public sealed class ProjectLinkTests
{
    private const string Root = "59dc947b0dba097d8d1dca3121154d3839dc3983";
    private static readonly Guid Linux = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Windows = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly DateTimeOffset Now = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Owner = Guid.NewGuid();

    private static ProjectIdentity Plain(Guid machine, string folder) =>
        ProjectIdentity.From(machine, new ProjectFacts { WorkingDirectory = folder, Directory = folder });

    private static ProjectIdentity Git(string folder) => ProjectIdentity.From(Linux, new ProjectFacts
    {
        WorkingDirectory = folder, Directory = folder, IsGit = true, RootCommit = Root, Roots = [Root], MainWorktree = folder,
        Remotes = [new GitRemote("origin", "https://github.com/fork/widget.git")],
    });

    [Fact]
    public void A_link_wins_over_the_folder_key()
    {
        var notes = Plain(Linux, "/home/me/notes");
        var own = Project.Create(Owner, "notes", Now);
        own.Attach(ProjectKeyKind.Folder, notes.FolderKey, Now);
        var target = Project.Create(Owner, "github.com/acme/widget", Now);
        target.Link(notes.FolderKey, Now);

        var match = ProjectMatcher.Match(notes, [.. own.Keys, .. target.Keys], []);

        Assert.Equal(new ProjectMatch(target.Id, ProjectRecognition.Link), match);
        Assert.Empty(ProjectMatcher.KeysToAttach(notes, match.RecognisedBy));
    }

    [Fact]
    public void A_link_wins_over_the_root_commit_so_a_fork_can_be_kept_apart()
    {
        var fork = Git("/src/widget-fork");
        var upstream = Project.Create(Owner, "github.com/acme/widget", Now);
        upstream.Attach(ProjectKeyKind.RootCommit, Root, Now);
        var apart = Project.Create(Owner, "fork", Now);
        apart.Link(fork.FolderKey, Now);

        var match = ProjectMatcher.Match(fork, [.. upstream.Keys, .. apart.Keys], []);

        Assert.Equal(new ProjectMatch(apart.Id, ProjectRecognition.Link), match);
        Assert.False(ProjectMatcher.Renames(fork, match.RecognisedBy));
    }

    [Fact]
    public void Linking_and_unlinking_raise_an_event_with_the_folder_key()
    {
        var project = Project.Create(Owner, "w", Now);

        project.Link("k", Now);
        Assert.True(project.Unlink("k", Now));
        Assert.False(project.Unlink("k", Now));

        Assert.Equal([new FolderLinkChanged(Owner, "k"), new FolderLinkChanged(Owner, "k")], project.DomainEvents);
        Assert.Empty(project.Keys);
    }

    [Fact]
    public void A_folder_key_splits_back_into_machine_and_path()
    {
        var key = ProjectIdentity.FolderKeyFor(Windows, @"C:\src\protostar");

        Assert.True(ProjectIdentity.TryParseFolderKey(key, out var machine, out var folder));
        Assert.Equal(Windows, machine);
        Assert.Equal(@"C:\src\protostar", folder);
        Assert.False(ProjectIdentity.TryParseFolderKey("not-a-key", out _, out _));
    }

    [Fact]
    public void A_plain_folder_is_offered_projects_with_a_same_named_folder_elsewhere()
    {
        var linux = Plain(Linux, "/home/me/repos/protostar");
        var windows = Project.Create(Owner, "protostar", Now);
        windows.Attach(ProjectKeyKind.Folder, ProjectIdentity.FolderKeyFor(Windows, @"C:\Users\me\Protostar"), Now);
        var unrelated = Project.Create(Owner, "website", Now);
        unrelated.Attach(ProjectKeyKind.Folder, ProjectIdentity.FolderKeyFor(Windows, @"C:\website"), Now);

        var suggestions = ProjectSuggestions.For(linux, ProjectRecognition.New, null, [windows, unrelated]);

        var only = Assert.Single(suggestions);
        Assert.Equal(windows.Id, only.ProjectId);
        Assert.Contains(@"C:\Users\me\Protostar", only.Reason);
    }

    [Fact]
    public void A_plain_folder_is_offered_a_repo_whose_name_ends_in_its_folder_name()
    {
        var plain = Plain(Linux, "/tmp/plain/widget");
        var repo = Project.Create(Owner, "github.com/acme/widget", Now);
        repo.Attach(ProjectKeyKind.RootCommit, Root, Now);

        Assert.Equal(repo.Id, Assert.Single(ProjectSuggestions.For(plain, ProjectRecognition.Folder, Guid.NewGuid(), [repo])).ProjectId);
    }

    [Fact]
    public void Nothing_is_suggested_for_a_git_checkout_a_linked_folder_or_the_folders_own_project()
    {
        var plain = Plain(Linux, "/tmp/plain/widget");
        var own = Project.Create(Owner, "widget", Now);
        own.Attach(ProjectKeyKind.Folder, plain.FolderKey, Now);

        Assert.Empty(ProjectSuggestions.For(Git("/src/widget"), ProjectRecognition.RootCommit, null, [own]));
        Assert.Empty(ProjectSuggestions.For(plain, ProjectRecognition.Link, null, [own]));
        Assert.Empty(ProjectSuggestions.For(plain, ProjectRecognition.Folder, own.Id, [own]));
    }
}
