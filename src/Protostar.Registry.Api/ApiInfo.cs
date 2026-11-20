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
/// Serialized with the default camelCase policy (<c>service</c>, <c>version</c>, <c>apiMajors</c>, <c>capabilities</c>).
/// </remarks>
/// <param name="Service">Always <c>protostar-registry</c>.</param>
/// <param name="Version">The registry build's version.</param>
/// <param name="ApiMajors">The API major versions this build serves. A client whose major is not listed stops with an upgrade hint.</param>
/// <param name="Capabilities">
/// The features this build serves within the API major, as stable kebab-case names (see <c>docs/api/capabilities.md</c>).
/// A client checks for a name before using its feature, and treats a missing name as "upgrade the registry". Names are
/// only ever added within v1, never removed.
/// </param>
public sealed record ApiMeta(string Service, string Version, int[] ApiMajors, IReadOnlyList<string> Capabilities);
