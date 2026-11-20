using Protostar.Registry.Api.Sessions;
using Xunit;

namespace Protostar.Registry.Tests.Sessions.Domain.ValueObjects;

/// <summary>Contract tests for <see cref="TranscriptKeyPrefix"/>.</summary>
public sealed class TranscriptKeyPrefixTests
{
    [Theory]
    [InlineData("7d3c")]
    [InlineData("7d3c9a52-1f4e")]
    [InlineData("7d3c_")]
    [InlineData("agent.a1b2")]
    public void A_prefix_of_at_least_four_key_characters_is_accepted(string raw)
    {
        Assert.True(TranscriptKeyPrefix.TryCreate(raw, out var prefix));
        Assert.Equal(raw, prefix.Value);
    }

    [Fact]
    public void A_prefix_is_kept_lowercased_so_matching_ignores_case()
    {
        Assert.True(TranscriptKeyPrefix.TryCreate("7D3C9A", out var prefix));

        Assert.Equal("7d3c9a", prefix.Value);
    }

    [Fact]
    public void Four_characters_is_the_minimum()
    {
        Assert.Equal(4, TranscriptKeyPrefix.MinLength);
        Assert.False(TranscriptKeyPrefix.TryCreate("7d3", out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("7d3c%")]
    [InlineData("7d3c 9a")]
    [InlineData("7d3c/9a")]
    [InlineData(".7d3c")]
    public void A_prefix_no_key_could_start_with_is_refused(string? raw) =>
        Assert.False(TranscriptKeyPrefix.TryCreate(raw, out _));

    [Fact]
    public void A_prefix_longer_than_any_key_is_refused() =>
        Assert.False(TranscriptKeyPrefix.TryCreate(new string('a', 201), out _));
}
