using Protostar.Registry.Api.Projects;
using Xunit;

namespace Protostar.Registry.Tests.Projects.Domain;

/// <summary>Contract tests for the <see cref="Project"/> aggregate and <see cref="ProjectFacts"/> validation.</summary>
public sealed class ProjectTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Attaching_a_key_twice_keeps_one()
    {
        var project = Project.Create(Guid.NewGuid(), "w", Now);

        Assert.True(project.Attach(ProjectKeyKind.RootCommit, new string('a', 40), Now));
        Assert.False(project.Attach(ProjectKeyKind.RootCommit, new string('a', 40), Now));
        Assert.Single(project.Keys);
    }

    [Fact]
    public void Keys_attached_by_different_projects_have_their_ids_in_the_same_order()
    {
        // EF inserts a table's new rows in id order, so this is what keeps two concurrent attaches from deadlocking.
        const string root = "0123456789abcdef0123456789abcdef01234567";
        const string remote = "github.com/acme/widget";
        int? order = null;
        for (var i = 0; i < 50; i++)
        {
            var project = Project.Create(Guid.NewGuid(), "w", Now);
            if (i % 2 == 0)
            {
                project.Attach(ProjectKeyKind.RootCommit, root, Now);
                project.Attach(ProjectKeyKind.Remote, remote, Now);
            }
            else
            {
                project.Attach(ProjectKeyKind.Remote, remote, Now);
                project.Attach(ProjectKeyKind.RootCommit, root, Now);
            }

            var rootId = project.Keys.Single(k => k.Kind == ProjectKeyKind.RootCommit).Id;
            var remoteId = project.Keys.Single(k => k.Kind == ProjectKeyKind.Remote).Id;
            order ??= Math.Sign(rootId.CompareTo(remoteId));
            Assert.Equal(order, Math.Sign(rootId.CompareTo(remoteId)));
        }
    }

    [Fact]
    public void A_key_attached_again_gets_a_new_id()
    {
        var project = Project.Create(Guid.NewGuid(), "w", Now);
        project.Link("m:/w", Now);
        var first = project.Keys.Single().Id;
        project.Unlink("m:/w", Now);
        project.Link("m:/w", Now);

        Assert.NotEqual(first, project.Keys.Single().Id);
    }

    [Fact]
    public void Attaching_a_remote_raises_an_event_so_shallow_clones_can_be_refiled()
    {
        var project = Project.Create(Guid.NewGuid(), "w", Now);

        project.Attach(ProjectKeyKind.Folder, "m:/w", Now);
        project.Attach(ProjectKeyKind.Remote, "github.com/acme/widget", Now);

        var raised = Assert.Single(project.DomainEvents);
        Assert.Equal(new ProjectRemoteAttached(project.OwnerId, project.Id, "github.com/acme/widget"), raised);
    }

    [Fact]
    public void Only_a_project_with_nothing_but_folder_keys_stands_only_for_folders()
    {
        var folder = Project.Create(Guid.NewGuid(), "w", Now);
        folder.Attach(ProjectKeyKind.Folder, "m:/w", Now);
        var repo = Project.Create(Guid.NewGuid(), "w", Now);
        repo.Attach(ProjectKeyKind.Folder, "m:/w", Now);
        repo.Attach(ProjectKeyKind.RootCommit, new string('a', 40), Now);

        Assert.True(folder.StandsOnlyForFolders);
        Assert.False(repo.StandsOnlyForFolders);
    }

    [Fact]
    public void A_blank_name_becomes_unnamed_and_a_long_one_is_cut()
    {
        Assert.Equal("unnamed", Project.Create(Guid.NewGuid(), "  ", Now).Name);
        Assert.Equal(Project.MaxNameLength, Project.Create(Guid.NewGuid(), new string('x', 900), Now).Name.Length);
    }

    [Fact]
    public void Facts_need_a_directory_and_full_commit_hashes()
    {
        Assert.NotNull(new ProjectFacts { WorkingDirectory = "/w" }.Problem());
        Assert.NotNull(new ProjectFacts { WorkingDirectory = "/w", Directory = "/w", RootCommit = "abc123" }.Problem());
        Assert.Null(new ProjectFacts { WorkingDirectory = "/w", Directory = "/w", RootCommit = new string('a', 40) }.Problem());
    }

    [Fact]
    public void Facts_round_trip_through_their_stored_form()
    {
        var facts = new ProjectFacts
        {
            WorkingDirectory = "/w/src", Directory = "/w/src", IsGit = true, RootCommit = new string('b', 40),
            Roots = [new string('b', 40)], Remotes = [new GitRemote("origin", "git@h:o/r.git")], Subpath = "src/",
        };

        var back = ProjectFacts.FromJson(facts.ToJson());

        Assert.Equal(facts.RootCommit, back.RootCommit);
        Assert.Equal(facts.Remotes, back.Remotes);
        Assert.Equal(facts.Subpath, back.Subpath);
        Assert.Contains("\"shallow\":false", facts.ToJson());
    }
}
