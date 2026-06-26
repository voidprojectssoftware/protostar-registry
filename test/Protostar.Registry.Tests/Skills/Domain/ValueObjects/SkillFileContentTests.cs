using System.Text;
using Protostar.Registry.Api.Skills;

namespace Protostar.Registry.Tests.Skills.Domain.ValueObjects;

public sealed class SkillFileContentTests
{
    [Fact]
    public void Create_stores_the_given_path()
    {
        var path = RelativePath.FromTrusted("dir/file.txt");
        var bytes = Encoding.UTF8.GetBytes("hello");

        var content = SkillFileContent.Create(path, bytes);

        Assert.Equal(path, content.Path);
    }

    [Fact]
    public void Create_size_equals_the_byte_length()
    {
        var path = RelativePath.FromTrusted("dir/file.txt");
        var bytes = Encoding.UTF8.GetBytes("hello world");

        var content = SkillFileContent.Create(path, bytes);

        Assert.Equal(bytes.LongLength, content.Size);
    }

    [Fact]
    public void Create_size_is_zero_for_empty_bytes()
    {
        var path = RelativePath.FromTrusted("dir/empty.txt");
        var bytes = Array.Empty<byte>();

        var content = SkillFileContent.Create(path, bytes);

        Assert.Equal(0L, content.Size);
    }

    [Fact]
    public void Create_hash_equals_Sha256Hash_Of_the_bytes()
    {
        // The key invariant the doc promises: the hash is "derived from the bytes",
        // so it can never drift from the content.
        var path = RelativePath.FromTrusted("dir/file.txt");
        var bytes = Encoding.UTF8.GetBytes("the quick brown fox");

        var content = SkillFileContent.Create(path, bytes);

        Assert.Equal(Sha256Hash.Of(bytes), content.Hash);
    }

    [Fact]
    public void Create_hash_equals_Sha256Hash_Of_empty_bytes_for_empty_content()
    {
        var path = RelativePath.FromTrusted("dir/empty.txt");
        var bytes = Array.Empty<byte>();

        var content = SkillFileContent.Create(path, bytes);

        Assert.Equal(Sha256Hash.Of(bytes), content.Hash);
    }

    [Fact]
    public void Create_different_bytes_yield_different_hashes()
    {
        var path = RelativePath.FromTrusted("dir/file.txt");
        var first = SkillFileContent.Create(path, Encoding.UTF8.GetBytes("content A"));
        var second = SkillFileContent.Create(path, Encoding.UTF8.GetBytes("content B"));

        Assert.NotEqual(first.Hash, second.Hash);
    }

    [Fact]
    public void Create_defensively_copies_input_bytes_so_content_and_hash_cannot_drift()
    {
        // The contract promises "size and hash can never drift from the content": Create copies the
        // input bytes, so mutating the caller's array after construction does not change the stored bytes.
        var path = RelativePath.FromTrusted("dir/file.txt");
        var bytes = Encoding.UTF8.GetBytes("original");
        var content = SkillFileContent.Create(path, bytes);

        bytes[0] = (byte)'X';

        Assert.Equal(Sha256Hash.Of(content.Bytes), content.Hash);
    }
}
