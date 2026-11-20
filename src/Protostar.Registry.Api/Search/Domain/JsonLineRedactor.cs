using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Protostar.Registry.Api.Search;

/// <summary>Applies <see cref="SecretRedactor"/> to JSONL content line by line, keeping every JSON line valid JSON.</summary>
/// <remarks>
/// <para>
/// Each line is read as JSON and <see cref="SecretRedactor.Redact"/> runs on the decoded text of every string value,
/// so a secret written with JSON escapes (a token spelled with <c>\u</c> escapes) is caught as the text it stands for.
/// JSON held inside a string, at any depth of escaping and anywhere in the string, is caught too: a string that is
/// itself a JSON document is redacted as JSON, and in any other string the assignment pattern reads a name and its
/// value through escaped quotes, so <c>curl -d "{\"password\":\"...\"}"</c> in a Bash command loses its password.
/// Only a string that changed is re-encoded and spliced back; every other byte of the line, and every line with
/// nothing to redact, comes out exactly as it went in.
/// </para>
/// <para>
/// Property names are never changed (they are a harness's schema), but they count. A string value under a name that
/// says it is secret (<see cref="SecretRedactor.IsSecretName"/>) is replaced whole, spaces and punctuation included,
/// so <c>{"password": "correct horse battery staple"}</c> becomes one marker. So is the <c>value</c> of an object
/// whose <c>name</c> or <c>key</c> is such a name, the shape Kubernetes and many other tools give environment
/// variables: <c>{"name": "DB_PASSWORD", "value": "..."}</c>. A value under any other name is still judged together
/// with it, so <c>"AccountKey": "..."</c> is caught as <c>AccountKey=...</c> would be in text.
/// </para>
/// <para>
/// A line that is not JSON (a corrupt line, a harness writing plain text) is redacted as plain text instead, since
/// there is no JSON syntax in it to keep. A string the redactor cannot finish scanning becomes
/// <c>[redacted:unscannable]</c>, so the line stays JSON and the string is never served as it was.
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

    private static void RedactLine(ReadOnlySpan<byte> line, IBufferWriter<byte> destination)
    {
        if (!TryRedactJson(line, destination))
            destination.Write(Encoding.UTF8.GetBytes(SecretRedactor.Redact(Encoding.UTF8.GetString(line))));
    }

    // A value is redacted alone, then once more as the assignment "name=value" when it has a property name, so a
    // secret whose only sign is its name is caught as it is in text. A name that says the value is secret replaces the
    // value whole: with the type the assignment found (aws_secret_access_key keeps aws-secret-key), else as a secret.
    // Values under four characters are left, as in text. Under any other name the assignment counts only when the
    // secret it finds starts the value.
    private static string RedactValue(string? name, string text)
    {
        var redacted = RedactText(text);
        if (name is null || LooksLikeJson(text))
            return redacted;

        var prefix = name + "=";
        var assignment = SecretRedactor.Redact(prefix + redacted);
        var named = assignment.StartsWith(prefix + "[redacted:", StringComparison.Ordinal) && !redacted.StartsWith("[redacted:", StringComparison.Ordinal);
        if (SecretRedactor.IsSecretName(name) && !IsOneMarker(redacted) && text.AsSpan().Trim().Length >= 4)
            return named ? assignment[prefix.Length..(assignment.IndexOf(']', prefix.Length) + 1)] : SecretRedactor.Marker("secret");

        return named ? assignment[prefix.Length..] : redacted;
    }

    private static bool IsOneMarker(string text) =>
        text.StartsWith("[redacted:", StringComparison.Ordinal) && text.IndexOf(']') == text.Length - 1;

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
            // For each open object (and, to keep the stack in step, array), the string under its "name" or "key"
            // property so far: what its "value" is called.
            var valueNames = new Stack<string?>();
            while (reader.Read())
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.PropertyName:
                        name = reader.GetString();
                        continue;
                    case JsonTokenType.StartObject or JsonTokenType.StartArray:
                        valueNames.Push(null);
                        break;
                    case JsonTokenType.EndObject or JsonTokenType.EndArray:
                        valueNames.Pop();
                        break;
                }

                if (reader.TokenType != JsonTokenType.String)
                {
                    name = null;
                    continue;
                }

                var text = reader.GetString()!;
                // A string with a property name is a member of the object on top of the stack.
                if (string.Equals(name, "name", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "key", StringComparison.OrdinalIgnoreCase))
                {
                    valueNames.Pop();
                    valueNames.Push(text);
                }
                else if (string.Equals(name, "value", StringComparison.OrdinalIgnoreCase) && valueNames.Peek() is { } valueName && SecretRedactor.IsSecretName(valueName))
                {
                    name = valueName;
                }

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
