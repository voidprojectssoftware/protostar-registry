using System.Globalization;

namespace Protostar.Registry.Api.Infrastructure;

/// <summary>Reads a date and time from a query string value as a UTC instant.</summary>
/// <remarks>
/// Npgsql writes a <see cref="DateTimeOffset"/> to a <c>timestamptz</c> parameter only when its offset is zero,
/// and throws otherwise, so a filter bound straight from the query string turned a valid
/// <c>2026-11-12T00:00:00-06:00</c> into a 500. Endpoints bind such parameters as text and read them through
/// here, which converts any offset to UTC and reports a malformed value so the endpoint can answer 400 with a
/// title that names the parameter, instead of ASP.NET Core's bare binding failure.
/// </remarks>
public static class QueryTimestamp
{
    /// <summary>The problem title for a malformed <paramref name="parameter"/>.</summary>
    public static string InvalidTitle(string parameter) =>
        $"'{parameter}' is not a valid date and time. Use ISO 8601, for example 2026-11-12T00:00:00Z or " +
        "2026-11-12T00:00:00-06:00, and encode a '+' offset as %2B.";

    /// <summary>Parses <paramref name="value"/> and converts it to UTC.</summary>
    /// <param name="value">The raw query string value. Null or empty means the parameter was not given.</param>
    /// <param name="utc">The instant with a zero offset, or null when <paramref name="value"/> is null or empty.</param>
    /// <returns>False when <paramref name="value"/> is present but is not a date and time.</returns>
    /// <remarks>A value without an offset is taken as UTC, so the answer does not depend on the server's time zone.</remarks>
    public static bool TryParseUtc(string? value, out DateTimeOffset? utc)
    {
        utc = null;
        if (string.IsNullOrEmpty(value))
            return true;

        if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
            return false;

        utc = parsed.ToUniversalTime();
        return true;
    }
}
