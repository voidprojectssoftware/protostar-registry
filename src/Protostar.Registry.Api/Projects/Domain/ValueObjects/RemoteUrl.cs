using System.Text.RegularExpressions;

namespace Protostar.Registry.Api.Projects;

/// <summary>Normalises git remote URLs so the HTTPS and SSH forms of one repository compare equal.</summary>
/// <remarks>
/// ADR 0002: the scheme, user, port and a trailing <c>.git</c> are dropped, <c>git@host:owner/repo</c> becomes
/// <c>host/owner/repo</c>, and the host is lowercased. So <c>https://github.com/Owner/Repo.git</c>,
/// <c>git@github.com:Owner/Repo.git</c> and <c>ssh://git@GitHub.com:22/Owner/Repo</c> all become
/// <c>github.com/Owner/Repo</c>. A local path stays a path, minus <c>.git</c>.
/// </remarks>
public static partial class RemoteUrl
{
    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9+.-]*://(?:[^@/]*@)?(?<host>[^/:]*)(?::\d*)?(?<path>/.*)?$")]
    private static partial Regex WithScheme();

    // scp-like syntax: [user@]host:path, where host is not a single letter (that is a Windows drive).
    [GeneratedRegex(@"^(?:[^@/\\]+@)?(?<host>[^/\\:]{2,}):(?<path>[^/\\].*)$")]
    private static partial Regex ScpLike();

    /// <summary>The normalised form of <paramref name="url"/>.</summary>
    public static string Normalise(string url)
    {
        var trimmed = url.Trim();
        string host;
        string path;

        if (WithScheme().Match(trimmed) is { Success: true } scheme)
        {
            host = scheme.Groups["host"].Value.ToLowerInvariant();
            path = scheme.Groups["path"].Value;
        }
        else if (ScpLike().Match(trimmed) is { Success: true } scp)
        {
            host = scp.Groups["host"].Value.ToLowerInvariant();
            path = scp.Groups["path"].Value;
        }
        else
        {
            return StripGit(trimmed.TrimEnd('/', '\\'));
        }

        path = StripGit(path.Trim('/'));
        return host.Length == 0 ? "/" + path : path.Length == 0 ? host : $"{host}/{path}";
    }

    private static string StripGit(string path) =>
        path.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? path[..^4].TrimEnd('/', '\\') : path;
}
