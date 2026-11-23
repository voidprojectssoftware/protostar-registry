namespace Protostar.Registry.Api.Sessions;

/// <summary>What a transcript's parsed lines say about it: the facts listings and search show.</summary>
/// <remarks>
/// <para>
/// Built incrementally: each appended chunk is parsed into a <see cref="TranscriptFacts"/> and folded in
/// with <see cref="Add"/>, so appends never re-read stored chunks to keep it current. Later facts win
/// for values that change during a session (title, branch, harness version); counts accumulate; the first
/// and last timestamps widen.
/// </para>
/// <para>
/// <see cref="ParserVersion"/> records which parsing built it. A summary started from <see cref="Empty"/> is
/// current; one stored by an older registry is not, and the reprocessor rebuilds it from the stored chunks.
/// </para>
/// </remarks>
public sealed record TranscriptSummary
{
    /// <summary>The version of the parsing that builds summaries.</summary>
    /// <remarks>
    /// Bump it whenever a change to a parser (or to <see cref="Add"/>) would give an existing transcript a different
    /// summary. Summaries of an older version are rebuilt in the background by <c>TranscriptReprocessor</c>. If the
    /// change also alters the text the search index gets, bump <c>SearchEntry.CurrentIndexVersion</c> too.
    /// <para>Version 2 added <see cref="Skills"/>, so transcripts parsed before it gain the skills they used.</para>
    /// <para>Version 3 added <see cref="Agents"/>, so transcripts parsed before it gain the agents they delegated to.</para>
    /// </remarks>
    public const int CurrentParserVersion = 3;

    /// <summary>The summary of a transcript with no lines yet, at <see cref="CurrentParserVersion"/>.</summary>
    public static readonly TranscriptSummary Empty = new() { ParserVersion = CurrentParserVersion };

    /// <summary>The <see cref="CurrentParserVersion"/> the summary was built with; 0 for summaries stored before versions existed.</summary>
    /// <remarks><see cref="Add"/> keeps it, so appending to an old summary never passes it off as current.</remarks>
    public int ParserVersion { get; init; }

    /// <summary>Whether this registry's parsing built the summary, so there is nothing to rebuild.</summary>
    /// <remarks>A newer version counts as current: an older instance running beside a newer one must not rebuild its work.</remarks>
    public bool IsCurrent => ParserVersion >= CurrentParserVersion;

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

    /// <summary>The distinct skills the transcript invoked, in the order first seen; empty when none (or before version 2).</summary>
    /// <remarks>This transcript's own invocations only: a session's subagents keep theirs, and a read adds them up.</remarks>
    public IReadOnlyList<NamedUse> Skills { get; init; } = [];

    /// <summary>The distinct agent types the transcript delegated to, in the order first seen; empty when none (or before version 3).</summary>
    /// <remarks>
    /// Every type as written, built-in and plugin ones included; which are custom agents is decided when they are read
    /// (<see cref="SessionAgents"/>). As with <see cref="Skills"/>, a session's subagents keep their own.
    /// </remarks>
    public IReadOnlyList<NamedUse> Agents { get; init; } = [];

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
        Skills = NamedUse.Merge(Skills, facts.Skills),
        Agents = NamedUse.Merge(Agents, facts.Agents),
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

    /// <summary>The distinct skills the chunk's lines invoked.</summary>
    public IReadOnlyList<NamedUse> Skills { get; init; } = [];

    /// <summary>The distinct agent types the chunk's lines delegated to.</summary>
    public IReadOnlyList<NamedUse> Agents { get; init; } = [];

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
