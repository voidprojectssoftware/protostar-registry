using System.Linq;
using Protostar.Registry.Api.Skills;

namespace Protostar.Registry.Tests.Skills.Domain.Policies;

public sealed class SkillSizePolicyTests
{
    private static SkillFileContent File(string path, long size)
        => SkillFileContent.Create(RelativePath.FromTrusted(path), new byte[size]);

    // A set of files summing to exactly `totalBytes`, each within the per-file limit: as many
    // full MaxFileBytes files as fit, plus a remainder file for the leftover (0 bytes when it
    // divides evenly). The file COUNT is derived from the constants, so these stay correct if
    // MaxFileBytes or MaxVersionBytes (and their ratio) ever change.
    private static SkillFileContent[] FilesTotalingExactly(long totalBytes)
    {
        var fullFiles = (int)(totalBytes / SkillSizePolicy.MaxFileBytes);
        var remainder = totalBytes % SkillSizePolicy.MaxFileBytes;
        return Enumerable.Range(0, fullFiles)
            .Select(i => File($"a/{i}.bin", SkillSizePolicy.MaxFileBytes))
            .Append(File("a/remainder.bin", remainder))
            .ToArray();
    }

    [Fact]
    public void Exceeded_returns_null_when_all_files_are_within_the_limits()
    {
        var files = new[]
        {
            File("a/one.txt", SkillSizePolicy.MaxFileBytes / 4),
            File("a/two.txt", SkillSizePolicy.MaxFileBytes / 4),
        };

        var result = SkillSizePolicy.Exceeded(files);

        Assert.Null(result);
    }

    [Fact]
    // ASSUMES: an empty file list has nothing exceeding any limit, so it is within limits -> null.
    public void Exceeded_returns_null_for_an_empty_file_list()
    {
        var result = SkillSizePolicy.Exceeded(Array.Empty<SkillFileContent>());

        Assert.Null(result);
    }

    [Fact]
    // ASSUMES: a zero-byte file is not "over" any limit, so it is within limits -> null.
    public void Exceeded_returns_null_for_a_zero_byte_file()
    {
        var files = new[] { File("a/empty.txt", 0) };

        var result = SkillSizePolicy.Exceeded(files);

        Assert.Null(result);
    }

    [Fact]
    // ASSUMES: "over MaxFileBytes" means strictly greater is rejected; exactly equal is allowed.
    public void Exceeded_returns_null_when_a_file_is_exactly_at_the_per_file_limit()
    {
        var files = new[] { File("a/exact.bin", SkillSizePolicy.MaxFileBytes) };

        var result = SkillSizePolicy.Exceeded(files);

        Assert.Null(result);
    }

    [Fact]
    public void Exceeded_flags_a_single_file_over_the_per_file_limit()
    {
        var files = new[] { File("a/big.bin", SkillSizePolicy.MaxFileBytes + 1) };

        var result = SkillSizePolicy.Exceeded(files);

        Assert.False(string.IsNullOrEmpty(result));
    }

    [Fact]
    // ASSUMES: "over MaxVersionBytes in total" means strictly greater is rejected; exactly equal is allowed.
    public void Exceeded_returns_null_when_the_total_is_exactly_at_the_version_limit()
    {
        // Files summing to exactly MaxVersionBytes, each within the per-file limit, so only the
        // total rule is in play and the boundary (equal) is allowed.
        var files = FilesTotalingExactly(SkillSizePolicy.MaxVersionBytes);

        var result = SkillSizePolicy.Exceeded(files);

        Assert.Null(result);
    }

    [Fact]
    public void Exceeded_flags_files_whose_total_is_over_the_version_limit()
    {
        // One byte past the version limit, every file still within the per-file limit, so only
        // the total can flag it. Count derives from the constants, so it holds at any ratio.
        var files = FilesTotalingExactly(SkillSizePolicy.MaxVersionBytes + 1);

        var result = SkillSizePolicy.Exceeded(files);

        Assert.False(string.IsNullOrEmpty(result));
    }
}
