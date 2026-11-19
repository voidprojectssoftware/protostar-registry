using Protostar.Registry.Api.Projects;
using Protostar.Registry.Api.Skills;
using Xunit;

namespace Protostar.Registry.Tests.Skills.Domain;

/// <summary>Contract tests for <see cref="FolderSkillMove"/>: which project skills go with a folder when it is linked or unlinked (registry#39).</summary>
public sealed class FolderSkillMoveTests
{
    private const string Root = "59dc947b0dba097d8d1dca3121154d3839dc3983";
    private const string Notes = "11111111-1111-1111-1111-111111111111:/home/me/notes";
    private const string Other = "11111111-1111-1111-1111-111111111111:/home/me/other";
    private static readonly DateTimeOffset Now = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Owner = Guid.NewGuid();

    private static Project FolderProject(string folderKey)
    {
        var project = Project.Create(Owner, "notes", Now);
        project.Attach(ProjectKeyKind.Folder, folderKey, Now);
        return project;
    }

    private static Project RepoProject()
    {
        var project = Project.Create(Owner, "github.com/acme/widget", Now);
        project.Attach(ProjectKeyKind.RootCommit, Root, Now);
        return project;
    }

    [Fact]
    public void A_project_with_only_the_folders_own_keys_stands_only_for_it()
    {
        var own = FolderProject(Notes);
        var apart = Project.Create(Owner, "widget (fork)", Now);
        apart.Link(Notes, Now);
        var repo = RepoProject();
        var linkedInto = FolderProject(Other);
        linkedInto.Link(Notes, Now);

        Assert.True(own.StandsOnlyFor(Notes));
        Assert.True(apart.StandsOnlyFor(Notes));
        Assert.False(repo.StandsOnlyFor(Notes));
        Assert.False(linkedInto.StandsOnlyFor(Notes));
        Assert.False(own.StandsOnlyFor(Other));
    }

    [Fact]
    public void Leaving_its_own_project_the_folder_takes_every_skill_of_it()
    {
        var own = FolderProject(Notes);
        var skills = new[] { Skill.Create(Owner, "notes", Now, own.Id), Skill.Create(Owner, "deploy", Now, own.Id) };

        Assert.Equal(skills, FolderSkillMove.Following(own, skills, Notes));
    }

    [Fact]
    public void Leaving_a_shared_project_the_folder_takes_only_the_skills_it_carried_in()
    {
        var own = FolderProject(Notes);
        var repo = RepoProject();
        var carried = Skill.Create(Owner, "notes", Now, own.Id);
        FolderSkillMove.Move([carried], repo, Notes);
        var repos = Skill.Create(Owner, "deploy", Now, repo.Id);

        Assert.Equal([carried], FolderSkillMove.Following(repo, [carried, repos], Notes));
        Assert.Empty(FolderSkillMove.Following(repo, [carried, repos], Other));
    }

    [Fact]
    public void A_skill_in_a_shared_project_remembers_the_folder_that_carried_it()
    {
        var own = FolderProject(Notes);
        var repo = RepoProject();
        var skill = Skill.Create(Owner, "notes", Now, own.Id);

        FolderSkillMove.Move([skill], repo, Notes);

        Assert.Equal(repo.Id, skill.ProjectId);
        Assert.Equal(Notes, skill.CarriedByFolderKey);
    }

    [Fact]
    public void Link_then_unlink_puts_the_skill_back_and_forgets_the_carrier()
    {
        var own = FolderProject(Notes);
        var repo = RepoProject();
        var skill = Skill.Create(Owner, "notes", Now, own.Id);

        FolderSkillMove.Move(FolderSkillMove.Following(own, [skill], Notes), repo, Notes);
        FolderSkillMove.Move(FolderSkillMove.Following(repo, [skill], Notes), own, Notes);

        Assert.Equal(own.Id, skill.ProjectId);
        Assert.Null(skill.CarriedByFolderKey);
    }

    [Fact]
    public void A_fork_kept_apart_and_rejoined_round_trips_its_skills()
    {
        var repo = RepoProject();
        var apart = Project.Create(Owner, "widget (fork)", Now);
        apart.Link(Notes, Now);
        var skill = Skill.Create(Owner, "notes", Now, apart.Id);

        FolderSkillMove.Move(FolderSkillMove.Following(apart, [skill], Notes), repo, Notes);
        Assert.Equal(repo.Id, skill.ProjectId);

        var apartAgain = Project.Create(Owner, "widget (fork)", Now);
        apartAgain.Link(Notes, Now);
        FolderSkillMove.Move(FolderSkillMove.Following(repo, [skill], Notes), apartAgain, Notes);
        Assert.Equal(apartAgain.Id, skill.ProjectId);
    }

    [Fact]
    public void A_name_the_destination_has_for_the_same_creator_clashes_and_others_do_not()
    {
        var own = FolderProject(Notes);
        var arriving = new[] { Skill.Create(Owner, "notes", Now, own.Id), Skill.Create(Owner, "deploy", Now, own.Id) };

        var clashes = FolderSkillMove.Clashes(arriving, [(Owner, "notes"), (Guid.NewGuid(), "deploy"), (Owner, "Deploy")]);

        Assert.Equal(["notes"], clashes);
    }

    [Fact]
    public void A_global_skill_never_moves_with_a_folder()
    {
        var global = Skill.Create(Owner, "notes", Now);

        Assert.Throws<InvalidOperationException>(() => global.MoveWithFolder(Guid.NewGuid(), null));
    }
}
