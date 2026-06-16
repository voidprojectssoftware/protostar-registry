using System.Security.Cryptography;
using System.Text;

namespace Protostar.Registry.Api.Skills;

/// <summary>
/// A SHA-256 digest as an uppercase hex string. A value object: two hashes are equal when their values
/// are, which is what lets a re-push be recognized as unchanged.
/// </summary>
public sealed record Sha256Hash
{
    private Sha256Hash(string value) => Value = value;

    public string Value { get; }

    /// <summary>The hash of a single file's bytes.</summary>
    public static Sha256Hash Of(byte[] content) => new(Convert.ToHexString(SHA256.HashData(content)));

    /// <summary>
    /// A stable hash over a set of files: each file's path and content-hash, ordered by path, so the same
    /// files in any upload order produce the same digest.
    /// </summary>
    public static Sha256Hash OfFiles(IEnumerable<SkillFileContent> files)
    {
        var builder = new StringBuilder();
        foreach (var file in files.OrderBy(f => f.Path.Value, StringComparer.Ordinal))
            builder.Append(file.Path.Value).Append('\n').Append(file.Hash.Value).Append('\n');

        return new Sha256Hash(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))));
    }

    /// <summary>Rehydrates a hash already computed when it was stored. For the persistence layer only.</summary>
    public static Sha256Hash FromTrusted(string value) => new(value);

    public override string ToString() => Value;
}
