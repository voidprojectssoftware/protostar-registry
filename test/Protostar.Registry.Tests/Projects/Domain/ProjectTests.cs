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
    public void Attaching_a_remote_raises_an_event_so_shallow_clones_can_be_refiled()
    {
        var project = Project.Create(Guid.NewGuid(), "w", Now);

        project.Attach(ProjectKeyKind.Folder, "m:/w", Now);
        project.Attach(ProjectKeyKind.Remote, "github.com/acme/widget", Now);

        var raised = Assert.Single(project.DomainEvents);
        Assert.Equal(new ProjectRemoteAttached(project.OwnerId, project.Id, "github.com/acme/widget"), raised);
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
