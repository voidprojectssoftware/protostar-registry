using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Protostar.Registry.Api.Infrastructure;

/// <summary>Stores a <see cref="DateTimeOffset"/> as the same instant with a zero offset.</summary>
/// <remarks>
/// Npgsql writes a <see cref="DateTimeOffset"/> to <c>timestamptz</c> only when its offset is zero and throws
/// otherwise, so a value parsed from a transcript line with a <c>-06:00</c> timestamp turned a backup into a 500.
/// <see cref="RegistryDbContext"/> applies this to every <see cref="DateTimeOffset"/> property by convention, so
/// no parser or new field has to remember to normalise. <c>timestamptz</c> keeps only the instant, so nothing
/// is lost; values read back are already UTC and pass through unchanged. The column type does not change.
/// </remarks>
public sealed class UtcDateTimeOffsetConverter() : ValueConverter<DateTimeOffset, DateTimeOffset>(
    value => value.ToUniversalTime(),
    value => value.ToUniversalTime());
