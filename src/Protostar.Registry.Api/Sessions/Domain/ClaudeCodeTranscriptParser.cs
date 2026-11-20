using System.Text;
using System.Text.Json;

namespace Protostar.Registry.Api.Sessions;

/// <summary>Reads Claude Code's JSONL transcript format.</summary>
/// <remarks>
/// Each line is a JSON object with a <c>type</c>. The ones read here: <c>user</c> and <c>assistant</c>
/// messages (with <c>timestamp</c>, <c>cwd</c>, <c>gitBranch</c> and <c>version</c>), and <c>ai-title</c>
/// (<c>aiTitle</c>). A <c>user</c> line whose content is tool results, or that is marked <c>isMeta</c>, is not
/// counted as a prompt. Every other type is counted as a line and otherwise ignored, so new record types
/// never break parsing.
/// <para>
/// The readable text (<see cref="TranscriptFacts.Text"/>) is the session title, what the user typed, the
/// assistant's text replies, and the names of the tools it called, one per line. Tool inputs and results,
/// thinking, and harness-injected lines are left out: they are bulky, rarely what someone searches for, and
/// the likeliest place for pasted data.
/// </para>
/// </remarks>
public sealed class ClaudeCodeTranscriptParser : ITranscriptParser
{
    public const string Harness = "claude-code";

    private const int PromptPreviewLength = 200;

    public TranscriptFacts Parse(ReadOnlySpan<byte> content)
    {
        var facts = new TranscriptFacts();
        var text = new StringBuilder();
        while (!content.IsEmpty)
        {
            var end = content.IndexOf((byte)'\n');
            var line = end < 0 ? content : content[..end];
            content = end < 0 ? [] : content[(end + 1)..];
            facts = Read(line, facts with { Lines = facts.Lines + 1 }, text);
        }

        return facts with { Text = text.ToString() };
    }

    private static TranscriptFacts Read(ReadOnlySpan<byte> line, TranscriptFacts facts, StringBuilder text)
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
            {
                if (String(root, "aiTitle") is not { Length: > 0 } title)
                    return facts;
                text.AppendLine(title);
                return facts with { Title = title };
            }

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
            {
                AppendReply(root, text);
                return facts with { AssistantMessages = facts.AssistantMessages + 1 };
            }

            if (PromptText(root) is not { } prompt)
                return facts;

            AppendPrompt(root, text);
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

    // Every text block of a prompt the user typed (PromptText returns only the first).
    private static void AppendPrompt(JsonElement root, StringBuilder text)
    {
        var content = root.GetProperty("message").GetProperty("content");
        if (content.ValueKind == JsonValueKind.String)
        {
            text.AppendLine(content.GetString());
            return;
        }

        foreach (var block in content.EnumerateArray())
        {
            if (String(block, "type") == "text" && String(block, "text") is { Length: > 0 } t)
                text.AppendLine(t);
        }
    }

    // The assistant's text blocks, and its tool calls by name only.
    private static void AppendReply(JsonElement root, StringBuilder text)
    {
        if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object ||
            !message.TryGetProperty("content", out var content))
            return;

        if (content.ValueKind == JsonValueKind.String)
        {
            text.AppendLine(content.GetString());
            return;
        }

        if (content.ValueKind != JsonValueKind.Array)
            return;

        foreach (var block in content.EnumerateArray())
        {
            switch (String(block, "type"))
            {
                case "text" when String(block, "text") is { Length: > 0 } t:
                    text.AppendLine(t);
                    break;
                case "tool_use" when String(block, "name") is { Length: > 0 } name:
                    text.Append("Tool: ").AppendLine(name);
                    break;
            }
        }
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
