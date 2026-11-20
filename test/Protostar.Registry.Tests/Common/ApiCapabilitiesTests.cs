using System.Reflection;
using System.Text.RegularExpressions;
using Protostar.Registry.Api;
using Xunit;

namespace Protostar.Registry.Tests.Common;

/// <summary>Contract tests for <see cref="ApiCapabilities"/>: stable, unique, kebab-case names, every one advertised.</summary>
public sealed partial class ApiCapabilitiesTests
{
    private static IReadOnlyList<string> Declared() =>
        typeof(ApiCapabilities).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field is { IsLiteral: true } && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToList();

    [Fact]
    public void Every_name_is_lowercase_kebab_case()
    {
        Assert.All(ApiCapabilities.All, name => Assert.Matches(KebabCase(), name));
    }

    [Fact]
    public void No_name_is_advertised_twice()
    {
        Assert.Equal(ApiCapabilities.All.Count, ApiCapabilities.All.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Every_declared_name_is_advertised_and_nothing_else()
    {
        var declared = Declared();

        Assert.Equal(declared.Count, declared.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(declared.Order(StringComparer.Ordinal), ApiCapabilities.All.Order(StringComparer.Ordinal));
    }

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex KebabCase();
}
