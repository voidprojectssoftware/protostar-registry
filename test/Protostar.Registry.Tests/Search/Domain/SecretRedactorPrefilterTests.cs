using Protostar.Registry.Api.Search;

namespace Protostar.Registry.Tests.Search.Domain;

/// <summary>Contract tests for how <see cref="SecretRedactor"/> declares its prefilters.</summary>
/// <remarks>That a prefilter never changes the output is checked on every sample, through <see cref="PrefilterCheck"/>.</remarks>
public sealed class SecretRedactorPrefilterTests
{
    [Fact]
    public void Every_pattern_declares_a_prefilter()
    {
        Assert.Equal(SecretRedactor.Types, SecretRedactor.Prefilters.Select(p => p.Type));
        foreach (var (type, prefilter) in SecretRedactor.Prefilters)
            Assert.True(prefilter is not null, $"The pattern {type} declares no prefilter; give it Prefilter.None or Prefilter.AnyOf(...).");
    }

    [Fact]
    public void A_prefilter_in_use_lists_at_least_one_literal_and_none_blank()
    {
        foreach (var (type, prefilter) in SecretRedactor.Prefilters.Where(p => p.Prefilter is { Applies: true }))
        {
            Assert.True(prefilter!.Literals.Count > 0, $"The prefilter of {type} lists no literals.");
            Assert.All(prefilter.Literals, literal => Assert.False(string.IsNullOrEmpty(literal), $"The prefilter of {type} lists a blank literal."));
        }
    }
}
