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
/// <para>
/// Skills the session invoked (<see cref="TranscriptFacts.Skills"/>) are recognised two ways. The model invokes one
/// with the <c>Skill</c> tool: an assistant <c>tool_use</c> block named <c>Skill</c> whose <c>input.skill</c> is the
/// skill's name (<c>plugin:skill</c> for a plugin's). The user invokes one by typing <c>/name</c>: Claude Code writes a
/// user line holding <c>&lt;command-name&gt;/name&lt;/command-name&gt;</c>, then, only when the command is a skill, a
/// harness-injected user line starting <c>Base directory for this skill:</c> with the skill's content. Built-in and
/// plain custom commands write the first line without the second, so a command counts only when the next user line is
/// that one; anything else between (an assistant line, another user line) means the command was not a skill. A pair
/// split across two chunks is not counted; the harness writes both in the same turn, so they arrive together. The
/// injected line also follows a <c>Skill</c> tool call, but no command is pending then, so a call is never counted twice.
/// Only names and times are kept, never the arguments.
/// </para>
/// </remarks>
public sealed class ClaudeCodeTranscriptParser : ITranscriptParser
{
    public const string Harness = "claude-code";

    private const int PromptPreviewLength = 200;

    private const string SkillToolName = "Skill";
    private const string SkillContentPrefix = "Base directory for this skill:";
    private const string CommandNameOpen = "<command-name>";
    private const string CommandNameClose = "</command-name>";

    public TranscriptFacts Parse(ReadOnlySpan<byte> content)
    {
        var facts = new TranscriptFacts();
        var text = new StringBuilder();
        var skills = new SkillTally();
        while (!content.IsEmpty)
        {
            var end = content.IndexOf((byte)'\n');
            var line = end < 0 ? content : content[..end];
            content = end < 0 ? [] : content[(end + 1)..];
            facts = Read(line, facts with { Lines = facts.Lines + 1 }, text, skills);
        }

        return facts with { Text = text.ToString(), Skills = skills.Uses };
    }

    private static TranscriptFacts Read(ReadOnlySpan<byte> line, TranscriptFacts facts, StringBuilder text, SkillTally skills)
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
                skills.Pending = null;
                CountSkillCalls(root, timestamp, skills);
                AppendReply(root, text);
                return facts with { AssistantMessages = facts.AssistantMessages + 1 };
            }

            CountSkillCommand(root, timestamp, skills);
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

    // Every Skill tool call in an assistant line, by its input's skill name.
    private static void CountSkillCalls(JsonElement root, DateTimeOffset? timestamp, SkillTally skills)
    {
        if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object ||
            !message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            return;

        foreach (var block in content.EnumerateArray())
        {
            if (String(block, "type") == "tool_use" && String(block, "name") == SkillToolName &&
                block.TryGetProperty("input", out var input))
                skills.Count(String(input, "skill"), timestamp);
        }
    }

    // A user line either confirms the pending slash command as a skill (it is the skill's injected content) or ends it,
    // and may start a new one.
    private static void CountSkillCommand(JsonElement root, DateTimeOffset? timestamp, SkillTally skills)
    {
        var first = FirstText(root);
        if (skills.Pending is { } pending && first is not null && first.StartsWith(SkillContentPrefix, StringComparison.Ordinal))
        {
            skills.Count(pending.Name, pending.At);
            skills.Pending = null;
            return;
        }

        skills.Pending = CommandName(first) is { } name ? (name, timestamp) : null;
    }

    // The first text of a user line, whatever kind of user line it is.
    private static string? FirstText(JsonElement root)
    {
        if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object ||
            !message.TryGetProperty("content", out var content))
            return null;
        if (content.ValueKind == JsonValueKind.String)
            return content.GetString();
        if (content.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var block in content.EnumerateArray())
        {
            if (String(block, "type") == "text")
                return String(block, "text");
        }

        return null;
    }

    // The command in <command-name>/name</command-name>, without its slash.
    private static string? CommandName(string? text)
    {
        if (text is null)
            return null;
        var open = text.IndexOf(CommandNameOpen, StringComparison.Ordinal);
        if (open < 0)
            return null;
        var start = open + CommandNameOpen.Length;
        var close = text.IndexOf(CommandNameClose, start, StringComparison.Ordinal);
        return close < 0 ? null : text[start..close];
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

/// <summary>The skills one chunk invoked so far, and the slash command waiting to be confirmed as a skill.</summary>
internal sealed class SkillTally
{
    private readonly List<SkillUse> _uses = [];

    public IReadOnlyList<SkillUse> Uses => _uses;

    public (string Name, DateTimeOffset? At)? Pending { get; set; }

    /// <summary>Counts one invocation of <paramref name="raw"/>, as the harness wrote it, unless it cannot be a skill name.</summary>
    /// <remarks>
    /// A leading <c>/</c> is dropped, so <c>/deploy</c> and <c>deploy</c> are one skill. A blank name, one with
    /// whitespace, or one longer than <see cref="SkillUse.MaxNameLength"/> is not a name any harness writes, and is skipped.
    /// </remarks>
    public void Count(string? raw, DateTimeOffset? at)
    {
        var name = raw?.Trim().TrimStart('/');
        if (string.IsNullOrEmpty(name) || name.Length > SkillUse.MaxNameLength || name.Any(char.IsWhiteSpace))
            return;

        var merged = SkillUse.Merge(_uses, [SkillUse.Once(name, at)]);
        _uses.Clear();
        _uses.AddRange(merged);
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
