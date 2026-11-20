using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Protostar.Registry.Api.Search;

/// <summary>Applies <see cref="SecretRedactor"/> to JSONL content line by line, keeping every JSON line valid JSON.</summary>
/// <remarks>
/// <para>
/// Each line is read as JSON and <see cref="SecretRedactor.Redact"/> runs on the decoded text of every string value,
/// so a secret written with JSON escapes (a quoted <c>\"password\": \"...\"</c> inside a tool result, a token
/// spelled with <c>\u</c> escapes) is caught as the text it stands for. A string value that is itself a JSON document
/// is redacted the same way, to any depth, so a secret behind two layers of escapes is caught too. Only a string that
/// changed is re-encoded and spliced back; every other byte of the line, and every line with nothing to redact, comes
/// out exactly as it went in. Property names are never changed (they are a harness's schema), but they count: a value
/// is judged together with its name, so <c>"password": "..."</c> is caught as <c>password=...</c> would be in text.
/// </para>
/// <para>
/// A line that is not JSON (a corrupt line, a harness writing plain text) is redacted as plain text instead, since
/// there is no JSON syntax in it to keep.
/// </para>
/// </remarks>
public static class JsonLineRedactor
{
    // Relaxed escaping writes non-ASCII and HTML-sensitive characters as themselves, as harnesses do, so a redacted
    // string differs from the original only where the secret was. Its output is still valid JSON.
    private static readonly JavaScriptEncoder Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;

    /// <summary>Writes <paramref name="content"/> to <paramref name="destination"/> with secrets redacted, one line at a time.</summary>
    /// <remarks>Lines are split at <c>\n</c>, which is kept; a final line without one is redacted and written without one.</remarks>
    public static void Redact(ReadOnlySpan<byte> content, IBufferWriter<byte> destination)
    {
        while (!content.IsEmpty)
        {
            var end = content.IndexOf((byte)'\n');
            var line = end < 0 ? content : content[..end];
            RedactLine(line, destination);
            if (end < 0)
                break;

            destination.Write("\n"u8);
            content = content[(end + 1)..];
        }
    }

    /// <summary>Returns one JSONL line with secrets redacted; see the class remarks.</summary>
    public static byte[] RedactLine(ReadOnlySpan<byte> line)
    {
        var output = new ArrayBufferWriter<byte>(line.Length);
        RedactLine(line, output);
        return output.WrittenSpan.ToArray();
    }

    private static void RedactLine(ReadOnlySpan<byte> line, IBufferWriter<byte> destination)
    {
        if (!TryRedactJson(line, destination))
            destination.Write(Encoding.UTF8.GetBytes(SecretRedactor.Redact(Encoding.UTF8.GetString(line))));
    }

    // A value is redacted alone, then once more as the assignment "name=value" when it has a property name, so a
    // secret whose only sign is its name ({"password": "hunter2"}) is caught as it is in text. The second pass counts
    // only when the secret it finds starts the value; otherwise (a name that is itself redacted, a value starting with
    // a quote or a space that the assignment pattern would read past) the value redacted alone stands.
    private static string RedactValue(string? name, string text)
    {
        var redacted = RedactText(text);
        if (name is null || LooksLikeJson(text))
            return redacted;

        var prefix = name + "=";
        var assignment = SecretRedactor.Redact(prefix + redacted);
        return assignment.StartsWith(prefix + "[redacted:", StringComparison.Ordinal) && !redacted.StartsWith("[redacted:", StringComparison.Ordinal)
            ? assignment[prefix.Length..]
            : redacted;
    }

    private static bool LooksLikeJson(string text)
    {
        var start = text.AsSpan().TrimStart();
        return !start.IsEmpty && (start[0] == '{' || start[0] == '[');
    }

    // A string value that is itself a JSON document (a tool's JSON output, a stringified payload) is redacted as JSON,
    // because its inner strings carry a second layer of escapes (\u0022, \") that would hide a secret's assignment
    // from the patterns. Anything else is redacted as text. Returns the same instance when nothing changed.
    private static string RedactText(string text)
    {
        if (!LooksLikeJson(text))
            return SecretRedactor.Redact(text);

        var bytes = Encoding.UTF8.GetBytes(text);
        var output = new ArrayBufferWriter<byte>(bytes.Length);
        if (!TryRedactJson(bytes, output))
            return SecretRedactor.Redact(text);

        return output.WrittenSpan.SequenceEqual(bytes) ? text : Encoding.UTF8.GetString(output.WrittenSpan);
    }

    // Copies the line to destination, replacing each string value whose redaction differs. Returns false, having
    // written nothing, when the line is not a single complete JSON value.
    private static bool TryRedactJson(ReadOnlySpan<byte> line, IBufferWriter<byte> destination)
    {
        var replacements = new List<(int Start, int Length, string Value)>();
        try
        {
            var reader = new Utf8JsonReader(line, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Disallow });
            string? name = null;
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.PropertyName)
                {
                    name = reader.GetString();
                    continue;
                }

                if (reader.TokenType != JsonTokenType.String)
                {
                    name = null;
                    continue;
                }

                var text = reader.GetString()!;
                var redacted = RedactValue(name, text);
                name = null;
                if (!ReferenceEquals(redacted, text))
                    replacements.Add(((int)reader.TokenStartIndex, reader.ValueSpan.Length + 2, redacted));
            }

            // A blank line, or one holding more than one value, is not a JSON line.
            if (reader.BytesConsumed == 0 || reader.CurrentDepth != 0)
                return false;
        }
        catch (JsonException)
        {
            return false;
        }

        var copied = 0;
        foreach (var (start, length, value) in replacements)
        {
            destination.Write(line[copied..start]);
            destination.Write("\""u8);
            destination.Write(JsonEncodedText.Encode(value, Encoder).EncodedUtf8Bytes);
            destination.Write("\""u8);
            copied = start + length;
        }

        destination.Write(line[copied..]);
        return true;
    }
}
