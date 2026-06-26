using System.Reflection;

namespace Protostar.Registry.Api;

/// <summary>
/// The registry's version, read at runtime from the assembly attribute MinVer stamps in at
/// build time (never hardcoded). Build metadata ("+&lt;git-sha&gt;") is trimmed for display.
/// </summary>
internal static class ApiInfo
{
    public static string Version { get; } = Resolve();

    /// <summary>The API major versions this build serves; advertised at <c>/v1/meta</c>.</summary>
    public static int[] ApiMajors { get; } = [1];

    private static string Resolve()
    {
        var info = typeof(ApiInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrEmpty(info))
            return "0.0.0";
        var plus = info.IndexOf('+');
        return plus >= 0 ? info[..plus] : info;
    }
}

/// <summary>The registry's identity and API-compatibility surface, returned by <c>/v1/meta</c>.</summary>
/// <remarks>
/// <c>ApiMajors</c> lists the API major versions this build serves; the CLI checks it on connect to
/// decide compatibility. Serialized with the default camelCase policy (<c>service</c>, <c>version</c>,
/// <c>apiMajors</c>).
/// </remarks>
public sealed record ApiMeta(string Service, string Version, int[] ApiMajors);
