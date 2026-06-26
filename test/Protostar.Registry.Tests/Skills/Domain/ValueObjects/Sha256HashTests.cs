using System.Linq;
using System.Text;
using Protostar.Registry.Api.Skills;

namespace Protostar.Registry.Tests.Skills.Domain.ValueObjects;

public sealed class Sha256HashTests
{
    private static SkillFileContent File(string path, byte[] bytes) =>
        SkillFileContent.Create(RelativePath.FromTrusted(path), bytes);

    private static SkillFileContent File(string path, string text) =>
        File(path, Encoding.UTF8.GetBytes(text));

    private static bool IsUppercaseHex(string value) =>
        value.All(c => (c >= '0' && c <= '9') || (c >= 'A' && c <= 'F'));

    // ---- Of: determinism ------------------------------------------------

    [Fact]
    public void Of_is_deterministic_for_the_same_bytes()
    {
        var bytes = Encoding.UTF8.GetBytes("hello world");

        Assert.Equal(Sha256Hash.Of(bytes), Sha256Hash.Of((byte[])bytes.Clone()));
    }

    [Fact]
    public void Of_produces_different_hashes_for_different_bytes()
    {
        var a = Sha256Hash.Of(Encoding.UTF8.GetBytes("alpha"));
        var b = Sha256Hash.Of(Encoding.UTF8.GetBytes("beta"));

        Assert.NotEqual(a, b);
    }

    // ---- Of: documented format -----------------------------------------

    [Fact]
    public void Of_value_is_64_characters_long()
    {
        var hash = Sha256Hash.Of(Encoding.UTF8.GetBytes("anything"));

        Assert.Equal(64, hash.Value.Length);
    }

    [Fact]
    public void Of_value_is_uppercase_hex()
    {
        var hash = Sha256Hash.Of(Encoding.UTF8.GetBytes("anything"));

        Assert.True(IsUppercaseHex(hash.Value));
    }

    [Fact]
    public void Of_empty_bytes_matches_the_well_known_sha256_digest()
    {
        // Environmental fact: SHA-256 of empty input is a documented constant.
        var hash = Sha256Hash.Of(System.Array.Empty<byte>());

        Assert.Equal("E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855", hash.Value);
    }

    // ---- OfFiles: order independence -----------------------------------

    [Fact]
    public void OfFiles_is_independent_of_file_order()
    {
        var one = File("a/first.txt", "one");
        var two = File("b/second.txt", "two");

        var forward = Sha256Hash.OfFiles(new[] { one, two });
        var reversed = Sha256Hash.OfFiles(new[] { two, one });

        Assert.Equal(forward, reversed);
    }

    [Fact]
    public void OfFiles_is_deterministic_for_the_same_files()
    {
        var files = new[] { File("a/first.txt", "one"), File("b/second.txt", "two") };

        Assert.Equal(Sha256Hash.OfFiles(files), Sha256Hash.OfFiles(files));
    }

    // ---- OfFiles: sensitivity ------------------------------------------

    [Fact]
    public void OfFiles_changes_when_a_files_content_changes()
    {
        var original = Sha256Hash.OfFiles(new[] { File("a/first.txt", "one") });
        var mutated = Sha256Hash.OfFiles(new[] { File("a/first.txt", "ONE") });

        Assert.NotEqual(original, mutated);
    }

    [Fact]
    public void OfFiles_changes_when_a_files_path_changes()
    {
        var original = Sha256Hash.OfFiles(new[] { File("a/first.txt", "one") });
        var moved = Sha256Hash.OfFiles(new[] { File("a/renamed.txt", "one") });

        Assert.NotEqual(original, moved);
    }

    [Fact]
    public void OfFiles_of_an_empty_set_returns_a_stable_digest()
    {
        // An empty set hashes to a deterministic digest rather than throwing.
        var first = Sha256Hash.OfFiles(System.Array.Empty<SkillFileContent>());
        var second = Sha256Hash.OfFiles(System.Array.Empty<SkillFileContent>());

        Assert.Equal(first, second);
    }

    // ---- FromTrusted + ToString ----------------------------------------

    [Fact]
    public void FromTrusted_round_trips_the_value()
    {
        const string stored = "E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855";

        Assert.Equal(stored, Sha256Hash.FromTrusted(stored).Value);
    }

    [Fact]
    public void ToString_returns_the_value()
    {
        // ASSUMES: ToString() override exposes Value verbatim (value-object convention).
        const string stored = "E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855";
        var hash = Sha256Hash.FromTrusted(stored);

        Assert.Equal(hash.Value, hash.ToString());
    }

    // ---- Record value equality -----------------------------------------

    [Fact]
    public void Hashes_with_the_same_value_are_equal()
    {
        const string stored = "E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855";

        Assert.Equal(Sha256Hash.FromTrusted(stored), Sha256Hash.FromTrusted(stored));
    }

    [Fact]
    public void Hashes_with_different_values_are_not_equal()
    {
        var a = Sha256Hash.FromTrusted("E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855");
        var b = Sha256Hash.FromTrusted("0000000000000000000000000000000000000000000000000000000000000000");

        Assert.NotEqual(a, b);
    }
}
