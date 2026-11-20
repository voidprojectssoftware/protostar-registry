using Protostar.Registry.Api.Common;

namespace Protostar.Registry.Api.Sessions;

/// <summary>A chunk was appended to a transcript; carries the chunk's readable text for the search index.</summary>
/// <param name="OwnerId">The transcript's owner.</param>
/// <param name="TranscriptId">The transcript.</param>
/// <param name="ChunkId">The chunk appended.</param>
/// <param name="StartsRevision">Whether the chunk is the first of a revision (offset 0), so anything derived from earlier revisions is out of date.</param>
/// <param name="MachineId">The machine that sent the chunk.</param>
/// <param name="Text">The chunk's readable text (<see cref="TranscriptFacts.Text"/>), not redacted.</param>
/// <remarks>
/// The text travels with the event so a handler never has to fetch and decompress the chunk again. It is session
/// content and may hold secrets, so <see cref="ToString"/> leaves it out: the logging handler logs every event.
/// </remarks>
public sealed record TranscriptChunkAppended(Guid OwnerId, Guid TranscriptId, Guid ChunkId, bool StartsRevision, Guid MachineId, string? Text) : IDomainEvent
{
    public override string ToString() =>
        $"{nameof(TranscriptChunkAppended)} {{ TranscriptId = {TranscriptId}, ChunkId = {ChunkId}, StartsRevision = {StartsRevision}, TextLength = {Text?.Length ?? 0} }}";
}
