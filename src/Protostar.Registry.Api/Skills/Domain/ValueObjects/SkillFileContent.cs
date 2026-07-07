namespace Protostar.Registry.Api.Skills;

/// <summary>
/// One file's content headed into a skill version: a validated <see cref="RelativePath"/>, the bytes, and
/// their <see cref="Sha256Hash"/>. The hash is derived from the bytes at construction, so size and hash
/// can never drift from the content the way separate fields could.
/// </summary>
/// <remarks>
/// The bytes are copied on construction, so a caller that mutates the array it passed in cannot drift the
/// stored content away from its hash.
/// </remarks>
public sealed class SkillFileContent
{
    private SkillFileContent(RelativePath path, byte[] bytes, Sha256Hash hash)
    {
        Path = path;
        Bytes = (byte[])bytes.Clone();
        Hash = hash;
    }

    public RelativePath Path { get; }

    public byte[] Bytes { get; }

    public Sha256Hash Hash { get; }

    public long Size => Bytes.LongLength;

    public static SkillFileContent Create(RelativePath path, byte[] bytes) =>
        new(path, bytes, Sha256Hash.Of(bytes));
}
