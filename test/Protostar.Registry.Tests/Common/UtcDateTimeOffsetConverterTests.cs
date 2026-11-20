using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Protostar.Registry.Api.Infrastructure;

namespace Protostar.Registry.Tests.Common;

/// <summary>Contract tests for <see cref="UtcDateTimeOffsetConverter"/> and the convention that applies it.</summary>
public sealed class UtcDateTimeOffsetConverterTests
{
    private static readonly UtcDateTimeOffsetConverter Converter = new();

    [Theory]
    [InlineData("2026-11-12T00:00:00-06:00", "2026-11-12T06:00:00Z")]
    [InlineData("2026-11-12T00:00:00+05:30", "2026-11-11T18:30:00Z")]
    [InlineData("2026-11-12T06:00:00.123Z", "2026-11-12T06:00:00.123Z")]
    public void Writes_any_offset_as_the_same_instant_in_UTC(string value, string expectedUtc)
    {
        var stored = (DateTimeOffset)Converter.ConvertToProvider(DateTimeOffset.Parse(value))!;

        Assert.Equal(DateTimeOffset.Parse(expectedUtc), stored);
        Assert.Equal(TimeSpan.Zero, stored.Offset);
    }

    [Fact]
    public void Reads_back_in_UTC()
    {
        var read = (DateTimeOffset)Converter.ConvertFromProvider(new DateTimeOffset(2026, 11, 12, 6, 0, 0, TimeSpan.Zero))!;

        Assert.Equal(new DateTimeOffset(2026, 11, 12, 6, 0, 0, TimeSpan.Zero), read);
        Assert.Equal(TimeSpan.Zero, read.Offset);
    }

    [Fact]
    public void Every_DateTimeOffset_property_in_the_model_is_converted()
    {
        // Building the model needs no database: the connection string is never opened.
        using var context = new RegistryDbContextFactory().CreateDbContext([]);

        var properties = context.Model.GetEntityTypes()
            .SelectMany(e => e.GetProperties().Concat(e.GetComplexProperties().SelectMany(c => c.ComplexType.GetProperties())))
            .Where(p => Nullable.GetUnderlyingType(p.ClrType) == typeof(DateTimeOffset) || p.ClrType == typeof(DateTimeOffset))
            .ToList();

        // Includes Transcript.Summary's nullable StartedAt, the column issue #56 found failing.
        Assert.Contains(properties, p => p.Name == "StartedAt" && p.ClrType == typeof(DateTimeOffset?));
        Assert.All(properties, p => Assert.IsType<UtcDateTimeOffsetConverter>(p.GetValueConverter()));
    }
}
