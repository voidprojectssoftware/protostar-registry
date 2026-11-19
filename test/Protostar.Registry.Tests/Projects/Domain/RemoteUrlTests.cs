using Protostar.Registry.Api.Projects;
using Xunit;

namespace Protostar.Registry.Tests.Projects.Domain;

/// <summary>Contract tests for <see cref="RemoteUrl.Normalise"/>, from ADR 0002's rule.</summary>
public sealed class RemoteUrlTests
{
    [Theory]
    [InlineData("https://github.com/Owner/Repo.git")]
    [InlineData("https://github.com/Owner/Repo")]
    [InlineData("https://user@GitHub.com/Owner/Repo.git")]
    [InlineData("git@github.com:Owner/Repo.git")]
    [InlineData("ssh://git@github.com:22/Owner/Repo.git")]
    [InlineData("ssh://git@GITHUB.COM/Owner/Repo/")]
    public void The_https_and_ssh_forms_of_one_repo_are_equal(string url) =>
        Assert.Equal("github.com/Owner/Repo", RemoteUrl.Normalise(url));

    [Fact]
    public void The_path_keeps_its_case() =>
        Assert.NotEqual(RemoteUrl.Normalise("https://github.com/owner/repo"), RemoteUrl.Normalise("https://github.com/Owner/Repo"));

    [Theory]
    [InlineData("/srv/git/upstream.git", "/srv/git/upstream")]
    [InlineData("file:///srv/git/upstream.git", "/srv/git/upstream")]
    [InlineData(@"C:\repos\upstream.git", @"C:\repos\upstream")]
    public void A_local_path_stays_a_path_without_dot_git(string url, string expected) =>
        Assert.Equal(expected, RemoteUrl.Normalise(url));
}
