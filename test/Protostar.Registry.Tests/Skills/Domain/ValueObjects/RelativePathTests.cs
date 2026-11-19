using Protostar.Registry.Api.Skills;

namespace Protostar.Registry.Tests.Skills.Domain.ValueObjects;

public sealed class RelativePathTests
{
    [Fact]
    public void TryCreate_accepts_a_simple_nested_relative_path()
    {
        var ok = RelativePath.TryCreate("skills/foo/bar.json", out var path);

        Assert.True(ok);
    }

    [Fact]
    public void TryCreate_round_trips_a_simple_nested_relative_path_through_value()
    {
        RelativePath.TryCreate("skills/foo/bar.json", out var path);

        Assert.Equal("skills/foo/bar.json", path.Value);
    }

    [Fact]
    public void TryCreate_returns_a_non_null_path_on_success()
    {
        RelativePath.TryCreate("skills/foo/bar.json", out var path);

        Assert.NotNull(path);
    }

    [Fact]
    public void TryCreate_rejects_an_empty_string()
    {
        var ok = RelativePath.TryCreate("", out var path);

        Assert.False(ok);
    }

    [Fact]
    public void TryCreate_leaves_path_null_when_rejecting_an_empty_string()
    {
        RelativePath.TryCreate("", out var path);

        Assert.Null(path);
    }

    [Fact]
    public void TryCreate_rejects_a_null_raw_value()
    {
        // ASSUMES: null is treated as the "empty" rejection case (param is string?, contract rejects empty).
        var ok = RelativePath.TryCreate(null, out var path);

        Assert.False(ok);
    }

    [Fact]
    public void TryCreate_leaves_path_null_when_rejecting_a_null_raw_value()
    {
        // ASSUMES: null is treated as the "empty" rejection case.
        RelativePath.TryCreate(null, out var path);

        Assert.Null(path);
    }

    [Fact]
    public void TryCreate_rejects_an_absolute_rooted_path()
    {
        var ok = RelativePath.TryCreate("/etc/passwd", out var path);

        Assert.False(ok);
    }

    [Fact]
    public void TryCreate_leaves_path_null_when_rejecting_an_absolute_rooted_path()
    {
        RelativePath.TryCreate("/etc/passwd", out var path);

        Assert.Null(path);
    }

    [Fact]
    public void TryCreate_rejects_a_path_with_a_traversal_segment()
    {
        var ok = RelativePath.TryCreate("a/../b", out var path);

        Assert.False(ok);
    }

    [Fact]
    public void TryCreate_rejects_a_path_that_is_only_a_traversal_segment()
    {
        var ok = RelativePath.TryCreate("..", out var path);

        Assert.False(ok);
    }

    [Fact]
    public void TryCreate_rejects_a_leading_traversal_segment()
    {
        var ok = RelativePath.TryCreate("../secrets", out var path);

        Assert.False(ok);
    }

    [Fact]
    public void TryCreate_normalizes_backslashes_to_forward_slashes()
    {
        RelativePath.TryCreate("a\\b\\c", out var path);

        Assert.Equal("a/b/c", path.Value);
    }

    [Fact]
    public void TryCreate_rejects_a_whitespace_only_string()
    {
        var ok = RelativePath.TryCreate("   ", out var path);

        Assert.False(ok);
    }

    [Theory]
    [InlineData("C:/foo")]
    [InlineData(@"C:\foo")]
    public void TryCreate_rejects_a_windows_drive_rooted_path(string raw)
    {
        var ok = RelativePath.TryCreate(raw, out var path);

        Assert.False(ok);
    }

    [Fact]
    public void TryCreate_rejects_a_path_containing_a_colon()
    {
        var ok = RelativePath.TryCreate("a/foo:bar.txt", out var path);

        Assert.False(ok);
    }

    [Fact]
    public void TryCreate_rejects_a_mid_path_single_dot_segment()
    {
        var ok = RelativePath.TryCreate("a/./b", out var path);

        Assert.False(ok);
    }

    [Theory]
    [InlineData("./a", "a")]
    [InlineData("./a/b", "a/b")]
    public void TryCreate_strips_a_leading_dot_slash_segment(string raw, string expected)
    {
        RelativePath.TryCreate(raw, out var path);

        Assert.Equal(expected, path.Value);
    }

    [Fact]
    public void TryCreate_rejects_a_trailing_slash()
    {
        var ok = RelativePath.TryCreate("a/", out var path);

        Assert.False(ok);
    }

    [Theory]
    [InlineData(" a/b ")]
    [InlineData("a/b ")]
    [InlineData(" a/b")]
    [InlineData("a /b")]
    [InlineData("a/ b")]
    [InlineData("a/b\t")]
    public void TryCreate_rejects_a_segment_with_leading_or_trailing_whitespace(string raw)
    {
        // Trimming would store a path that differs from the file the client hashed, so it is refused instead.
        var ok = RelativePath.TryCreate(raw, out var path);

        Assert.False(ok);
        Assert.Null(path);
    }

    [Theory]
    [InlineData("a b/c d.md")]
    [InlineData("notes on x.md")]
    public void TryCreate_accepts_whitespace_inside_a_segment(string raw)
    {
        RelativePath.TryCreate(raw, out var path);

        Assert.Equal(raw, path.Value);
    }

    [Theory]
    [InlineData(".git/hooks/pre-commit")]
    [InlineData("scripts/node_modules/x/index.js")]
    [InlineData("__pycache__/a.pyc")]
    [InlineData(".venv/bin/python")]
    [InlineData(".hg/store")]
    [InlineData(".svn/entries")]
    [InlineData(".GIT/config")]
    [InlineData("Node_Modules/x.js")]
    [InlineData(".git")]
    public void TryCreate_rejects_a_path_through_a_folder_the_cli_never_reads(string raw)
    {
        // The CLI skips these folders when it reads a skill, so a file stored under one would be installed
        // but never read back, and the two sides could never agree on the skill's content.
        var ok = RelativePath.TryCreate(raw, out var path);

        Assert.False(ok);
        Assert.Null(path);
    }

    [Theory]
    [InlineData(".gitignore")]
    [InlineData("docs/.github/x.md")]
    [InlineData("my.git/x.md")]
    [InlineData("node_modules.md")]
    public void TryCreate_accepts_names_that_only_resemble_an_ignored_folder(string raw)
    {
        RelativePath.TryCreate(raw, out var path);

        Assert.Equal(raw, path.Value);
    }

    [Theory]
    [InlineData("foo..bar")]
    [InlineData("a/..b")]
    public void TryCreate_accepts_a_dot_dot_substring_that_is_not_a_whole_segment(string raw)
    {
        // A ".." that is part of a longer segment is a real filename, not a traversal segment,
        // so it is accepted and stored verbatim. Guards against a naive substring check.
        RelativePath.TryCreate(raw, out var path);

        Assert.Equal(raw, path.Value);
    }

    [Fact]
    public void FromTrusted_produces_an_instance_whose_value_is_the_input()
    {
        // ASSUMES: FromTrusted stores the given value verbatim (no re-validation, no re-normalization).
        var path = RelativePath.FromTrusted("skills/foo/bar.json");

        Assert.Equal("skills/foo/bar.json", path.Value);
    }

    [Fact]
    public void FromTrusted_produces_a_non_null_instance()
    {
        var path = RelativePath.FromTrusted("skills/foo/bar.json");

        Assert.NotNull(path);
    }

    [Fact]
    public void ToString_returns_the_value()
    {
        var path = RelativePath.FromTrusted("skills/foo/bar.json");

        Assert.Equal(path.Value, path.ToString());
    }

    [Fact]
    public void Equals_is_true_for_two_paths_with_the_same_value()
    {
        RelativePath.TryCreate("skills/foo/bar.json", out var first);
        RelativePath.TryCreate("skills/foo/bar.json", out var second);

        Assert.Equal(first, second);
    }

    [Fact]
    public void Equals_is_false_for_two_paths_with_different_values()
    {
        RelativePath.TryCreate("skills/foo/bar.json", out var first);
        RelativePath.TryCreate("skills/other/baz.json", out var second);

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void GetHashCode_matches_for_two_paths_with_the_same_value()
    {
        RelativePath.TryCreate("skills/foo/bar.json", out var first);
        RelativePath.TryCreate("skills/foo/bar.json", out var second);

        Assert.Equal(first.GetHashCode(), second.GetHashCode());
    }
}
