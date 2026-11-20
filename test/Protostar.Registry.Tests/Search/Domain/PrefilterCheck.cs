using Protostar.Registry.Api.Search;
using System.Buffers;

namespace Protostar.Registry.Tests.Search.Domain;

/// <summary>Redacts the way the registry does, and asserts the result is what it would be with no prefilters.</summary>
/// <remarks>
/// <see cref="SecretRedactor"/> skips a slow pattern on text holding none of its declared literals. A literal missing
/// from a list would silently skip a secret, so every sample the redactor tests use goes through here: the corpus is
/// every call these tests make, positive and negative samples and the seeded fuzz alike, and no separate list of
/// samples can fall behind them.
/// </remarks>
internal static class PrefilterCheck
{
    /// <summary><see cref="SecretRedactor.Redact"/>, checked against the run without prefilters.</summary>
    public static string Redact(string text)
    {
        string full;
        using (SecretRedactor.WithoutPrefilters())
            full = SecretRedactor.Redact(text);

        var filtered = SecretRedactor.Redact(text);
        Assert.True(full == filtered, $"The prefilters changed the output for: {text}\nwithout: {full}\nwith:    {filtered}");
        return filtered;
    }

    /// <summary><see cref="JsonLineRedactor.Redact"/>, checked against the run without prefilters.</summary>
    public static byte[] RedactJsonLines(ReadOnlySpan<byte> content)
    {
        var full = new ArrayBufferWriter<byte>();
        using (SecretRedactor.WithoutPrefilters())
            JsonLineRedactor.Redact(content, full);

        var filtered = new ArrayBufferWriter<byte>();
        JsonLineRedactor.Redact(content, filtered);
        Assert.True(full.WrittenSpan.SequenceEqual(filtered.WrittenSpan),
            $"The prefilters changed the output for: {System.Text.Encoding.UTF8.GetString(content)}");
        return filtered.WrittenSpan.ToArray();
    }
}
