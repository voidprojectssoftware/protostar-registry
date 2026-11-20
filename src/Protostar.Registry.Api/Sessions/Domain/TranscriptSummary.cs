namespace Protostar.Registry.Api.Sessions;

/// <summary>What a transcript's parsed lines say about it: the facts listings and search show.</summary>
/// <remarks>
/// Built incrementally: each appended chunk is parsed into a <see cref="TranscriptFacts"/> and folded in
/// with <see cref="Add"/>, so the registry never re-reads stored chunks to keep it current. Later facts win
/// for values that change during a session (title, branch, harness version); counts accumulate; the first
/// and last timestamps widen.
/// </remarks>
public sealed record TranscriptSummary
{
    public static readonly TranscriptSummary Empty = new();

    /// <summary>The harness's own title for the session, when it has written one.</summary>
    public string? Title { get; init; }

    /// <summary>The first thing the user asked, shortened; a fallback title.</summary>
    public string? FirstPrompt { get; init; }

    /// <summary>The working directory the session ran in, on the machine it ran on.</summary>
    public string? WorkingDirectory { get; init; }

    public string? GitBranch { get; init; }

    /// <summary>The harness version that last wrote to the transcript.</summary>
    public string? HarnessVersion { get; init; }

    public DateTimeOffset? StartedAt { get; init; }

    public DateTimeOffset? LastActivityAt { get; init; }

    /// <summary>Prompts the user typed (not tool results, which harnesses also record as user lines).</summary>
    public int UserMessages { get; init; }

    public int AssistantMessages { get; init; }

    /// <summary>Every line, including ones the parser does not interpret.</summary>
    public int Lines { get; init; }

    /// <summary>Lines that were not valid JSON.</summary>
    public int MalformedLines { get; init; }

    /// <summary>Folds the facts from one more chunk into this summary.</summary>
    public TranscriptSummary Add(TranscriptFacts facts) => this with
    {
        Title = facts.Title ?? Title,
        FirstPrompt = FirstPrompt ?? facts.FirstPrompt,
        WorkingDirectory = WorkingDirectory ?? facts.WorkingDirectory,
        GitBranch = facts.GitBranch ?? GitBranch,
        HarnessVersion = facts.HarnessVersion ?? HarnessVersion,
        StartedAt = Earliest(StartedAt, facts.FirstTimestamp),
        LastActivityAt = Latest(LastActivityAt, facts.LastTimestamp),
        UserMessages = UserMessages + facts.UserMessages,
        AssistantMessages = AssistantMessages + facts.AssistantMessages,
        Lines = Lines + facts.Lines,
        MalformedLines = MalformedLines + facts.MalformedLines,
    };

    private static DateTimeOffset? Earliest(DateTimeOffset? a, DateTimeOffset? b) => a is null ? b : b is null ? a : a < b ? a : b;

    private static DateTimeOffset? Latest(DateTimeOffset? a, DateTimeOffset? b) => a is null ? b : b is null ? a : a > b ? a : b;
}

/// <summary>What one chunk's lines say, as found by an <see cref="ITranscriptParser"/>.</summary>
public sealed record TranscriptFacts
{
    public string? Title { get; init; }
    public string? FirstPrompt { get; init; }
    public string? WorkingDirectory { get; init; }
    public string? GitBranch { get; init; }
    public string? HarnessVersion { get; init; }
    public DateTimeOffset? FirstTimestamp { get; init; }
    public DateTimeOffset? LastTimestamp { get; init; }
    public int UserMessages { get; init; }
    public int AssistantMessages { get; init; }
    public int Lines { get; init; }
    public int MalformedLines { get; init; }

    /// <summary>The chunk's readable text for the search index, one message per line, not redacted; <c>null</c> when the parser extracts none.</summary>
    /// <remarks>Not folded into <see cref="TranscriptSummary"/>: it goes to the index with the chunk and is not kept on the transcript.</remarks>
    public string? Text { get; init; }
}

/// <summary>Reads one harness's transcript format into <see cref="TranscriptFacts"/>.</summary>
/// <remarks>
/// The registry, not the client, understands each harness's format, so a parser fix reaches every
/// transcript without a client upgrade. A harness with no parser gets <see cref="LineCountingParser"/>.
/// </remarks>
public interface ITranscriptParser
{
    /// <summary>Parses whole lines of JSONL; <paramref name="content"/> ends with a newline.</summary>
    TranscriptFacts Parse(ReadOnlySpan<byte> content);
}

/// <summary>The parser for harnesses the registry does not understand yet: it only counts lines.</summary>
public sealed class LineCountingParser : ITranscriptParser
{
    public static readonly LineCountingParser Instance = new();

    public TranscriptFacts Parse(ReadOnlySpan<byte> content) => new() { Lines = content.Count((byte)'\n') };
}
