using Protostar.Registry.Api.Infrastructure;
using Xunit;

namespace Protostar.Registry.Tests.Common;

/// <summary>Contract tests for <see cref="QueryTimestamp"/>.</summary>
public sealed class QueryTimestampTests
{
    [Theory]
    [InlineData("2026-11-12T00:00:00-06:00", "2026-11-12T06:00:00Z")]
    [InlineData("2026-11-12T00:00:00+05:30", "2026-11-11T18:30:00Z")]
    [InlineData("2026-11-12T06:00:00Z", "2026-11-12T06:00:00Z")]
    [InlineData("2026-11-12T06:00:00.123+00:00", "2026-11-12T06:00:00.123Z")]
    public void A_value_with_any_offset_becomes_the_same_instant_in_UTC(string value, string expectedUtc)
    {
        Assert.True(QueryTimestamp.TryParseUtc(value, out var utc));

        var expected = DateTimeOffset.Parse(expectedUtc, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(expected, utc);
        Assert.Equal(TimeSpan.Zero, utc!.Value.Offset);
    }

    [Fact]
    public void A_value_without_an_offset_is_taken_as_UTC()
    {
        Assert.True(QueryTimestamp.TryParseUtc("2026-11-12T06:00:00", out var utc));

        Assert.Equal(new DateTimeOffset(2026, 11, 12, 6, 0, 0, TimeSpan.Zero), utc);
        Assert.Equal(TimeSpan.Zero, utc!.Value.Offset);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void A_missing_value_is_accepted_as_no_filter(string? value)
    {
        Assert.True(QueryTimestamp.TryParseUtc(value, out var utc));

        Assert.Null(utc);
    }

    [Theory]
    [InlineData("not-a-date")]
    [InlineData("2026-13-45")]
    [InlineData("2026-11-12T25:00:00Z")]
    [InlineData("2026-11-12T00:00:00 05:30")] // an unencoded "+05:30" after query string decoding
    public void A_malformed_value_is_refused(string value)
    {
        Assert.False(QueryTimestamp.TryParseUtc(value, out var utc));

        Assert.Null(utc);
    }

    [Fact]
    public void The_refusal_title_names_the_parameter()
    {
        Assert.StartsWith("'since' is not a valid date and time.", QueryTimestamp.InvalidTitle("since"));
    }
}
