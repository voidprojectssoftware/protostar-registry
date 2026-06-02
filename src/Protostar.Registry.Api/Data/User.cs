namespace Protostar.Registry.Api.Data;

/// <summary>
/// A registry user, identified by their GitHub account. Synced skills are tagged to this
/// identity and suggestions route back to it. The registry owns this record; GitHub is only
/// the upstream login.
/// </summary>
public sealed class User
{
    public Guid Id { get; set; }

    /// <summary>The user's numeric GitHub id (stable across login renames).</summary>
    public string GitHubId { get; set; } = default!;

    /// <summary>The user's GitHub login (handle). Refreshed on each sign-in.</summary>
    public string Login { get; set; } = default!;

    /// <summary>The user's display name from GitHub, if any.</summary>
    public string? Name { get; set; }

    public string? AvatarUrl { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
