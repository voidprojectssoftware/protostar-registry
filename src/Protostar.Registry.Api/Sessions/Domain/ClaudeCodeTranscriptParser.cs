using System.Text.Json;

namespace Protostar.Registry.Api.Sessions;

/// <summary>Reads Claude Code's JSONL transcript format.</summary>
/// <remarks>
/// Each line is a JSON object with a <c>type</c>. The ones read here: <c>user</c> and <c>assistant</c>
/// messages (with <c>timestamp</c>, <c>cwd</c>, <c>gitBranch</c> and <c>version</c>), and <c>ai-title</c>
/// (<c>aiTitle</c>). A <c>user</c> line whose content is tool results, or that is marked <c>isMeta</c>, is not
/// counted as a prompt. Every other type is counted as a line and otherwise ignored, so new record types
/// never break parsing.
/// </remarks>
public sealed class ClaudeCodeTranscriptParser : ITranscriptParser
{
    public const string Harness = "claude-code";

    private const int PromptPreviewLength = 200;

    public TranscriptFacts Parse(ReadOnlySpan<byte> content)
    {
        var facts = new TranscriptFacts();
        while (!content.IsEmpty)
        {
            var end = content.IndexOf((byte)'\n');
            var line = end < 0 ? content : content[..end];
            content = end < 0 ? [] : content[(end + 1)..];
            facts = Read(line, facts with { Lines = facts.Lines + 1 });
        }

        return facts;
    }

    private static TranscriptFacts Read(ReadOnlySpan<byte> line, TranscriptFacts facts)
    {
        if (line.TrimEnd((byte)'\r').Trim((byte)' ').IsEmpty)
            return facts;

        try
        {
            var reader = new Utf8JsonReader(line);
            using var doc = JsonDocument.ParseValue(ref reader);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return facts;

            var type = String(root, "type");
            if (type == "ai-title")
                return String(root, "aiTitle") is { Length: > 0 } title ? facts with { Title = title } : facts;

            if (type is not ("user" or "assistant"))
                return facts;

            var timestamp = root.TryGetProperty("timestamp", out var ts) && ts.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(ts.GetString(), out var parsed) ? parsed : (DateTimeOffset?)null;

            facts = facts with
            {
                WorkingDirectory = facts.WorkingDirectory ?? String(root, "cwd"),
                GitBranch = String(root, "gitBranch") ?? facts.GitBranch,
                HarnessVersion = String(root, "version") ?? facts.HarnessVersion,
                // Lines are not guaranteed to be in time order, so widen rather than take the last seen.
                FirstTimestamp = timestamp is { } t1 && (facts.FirstTimestamp is null || t1 < facts.FirstTimestamp) ? t1 : facts.FirstTimestamp,
                LastTimestamp = timestamp is { } t2 && (facts.LastTimestamp is null || t2 > facts.LastTimestamp) ? t2 : facts.LastTimestamp,
            };

            if (type == "assistant")
                return facts with { AssistantMessages = facts.AssistantMessages + 1 };

            if (PromptText(root) is not { } prompt)
                return facts;

            return facts with
            {
                UserMessages = facts.UserMessages + 1,
                FirstPrompt = facts.FirstPrompt ?? Shorten(prompt),
            };
        }
        catch (JsonException)
        {
            return facts with { MalformedLines = facts.MalformedLines + 1 };
        }
    }

    // The text the user typed, or null for tool results and harness-injected (isMeta) lines.
    private static string? PromptText(JsonElement root)
    {
        if (root.TryGetProperty("isMeta", out var meta) && meta.ValueKind == JsonValueKind.True)
            return null;
        if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object ||
            !message.TryGetProperty("content", out var content))
            return null;

        if (content.ValueKind == JsonValueKind.String)
            return content.GetString();
        if (content.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var block in content.EnumerateArray())
        {
            var blockType = String(block, "type");
            if (blockType == "tool_result")
                return null;
            if (blockType == "text" && String(block, "text") is { Length: > 0 } text)
                return text;
        }

        return null;
    }

    private static string? String(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string Shorten(string text)
    {
        var single = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return single.Length <= PromptPreviewLength ? single : single[..(PromptPreviewLength - 3)] + "...";
    }
}

/// <summary>Picks the parser for a harness.</summary>
public static class TranscriptParsers
{
    private static readonly ClaudeCodeTranscriptParser ClaudeCode = new();

    public static ITranscriptParser For(HarnessId harness) => harness.Value switch
    {
        ClaudeCodeTranscriptParser.Harness => ClaudeCode,
        _ => LineCountingParser.Instance,
    };
}
