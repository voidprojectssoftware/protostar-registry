using Protostar.Registry.Api.Agents;

namespace Protostar.Registry.Tests.Agents.Domain;

/// <summary>Contract tests for <see cref="AgentManifestParser"/>: name and description are required; tools and model are read when present.</summary>
public sealed class AgentManifestParserTests
{
    [Fact]
    public void Reads_name_description_tools_as_a_comma_list_and_model()
    {
        var (manifest, problem) = AgentManifestParser.Parse("---\nname: code-reviewer\ndescription: Reviews code.\ntools: Read, Grep, Glob\nmodel: sonnet\n---\nYou review code.\n");

        Assert.Null(problem);
        Assert.Equal("code-reviewer", manifest!.Name);
        Assert.Equal("Reviews code.", manifest.Description);
        Assert.Equal(["Read", "Grep", "Glob"], manifest.Tools);
        Assert.Equal("sonnet", manifest.Model);
    }

    [Fact]
    public void Tools_and_model_are_optional()
    {
        var (manifest, _) = AgentManifestParser.Parse("---\nname: a\ndescription: b\n---\n");

        Assert.Empty(manifest!.Tools);
        Assert.Null(manifest.Model);
    }

    [Theory]
    [InlineData("You review code.\n", "no YAML front matter")]
    [InlineData("---\ndescription: Reviews code.\n---\n", "has no 'name'")]
    [InlineData("---\nname: \"  \"\ndescription: Reviews code.\n---\n", "has no 'name'")]
    [InlineData("---\nname: reviewer\n---\n", "no 'description'")]
    public void A_file_without_front_matter_name_or_description_is_refused_saying_which(string markdown, string expected)
    {
        var (manifest, problem) = AgentManifestParser.Parse(markdown);

        Assert.Null(manifest);
        Assert.Contains(expected, problem);
    }
}
