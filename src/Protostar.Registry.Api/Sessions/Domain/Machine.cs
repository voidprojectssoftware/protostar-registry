namespace Protostar.Registry.Api.Sessions;

/// <summary>A computer or VM a user backs up from, identified by an id the client generates once and keeps.</summary>
/// <remarks>
/// The id comes from the client so a machine can register itself with an idempotent PUT and keep the same
/// identity across reinstalls of the CLI. Machine ids are unique per owner, not globally; two users can
/// never see or change each other's machines.
/// </remarks>
public sealed class Machine
{
    private Machine()
    {
    }

    /// <summary>The client-generated id.</summary>
    public Guid Id { get; private set; }

    public Guid OwnerId { get; private set; }

    /// <summary>A display name, usually the host name.</summary>
    public string Name { get; private set; } = default!;

    /// <summary>The operating system, for example <c>linux</c>, <c>windows</c> or <c>macos</c>.</summary>
    public string OperatingSystem { get; private set; } = default!;

    public DateTimeOffset RegisteredAt { get; private set; }

    /// <summary>When the machine last registered or sent content.</summary>
    /// <remarks>
    /// Never moves backwards. Concurrent requests each take their own clock reading, and the one saved last may
    /// have read the clock first; it must not wind the machine back to before a sighting already recorded.
    /// </remarks>
    public DateTimeOffset LastSeenAt { get; private set; }

    /// <summary>Registers a new machine.</summary>
    public static Machine Register(Guid id, Guid ownerId, string name, string operatingSystem, DateTimeOffset now)
    {
        if (id == Guid.Empty)
            throw new ArgumentException("A machine needs an id.", nameof(id));

        var machine = new Machine { Id = id, OwnerId = ownerId, RegisteredAt = now };
        machine.Update(name, operatingSystem, now);
        return machine;
    }

    /// <summary>Refreshes the machine's details, for example after a rename.</summary>
    public void Update(string name, string operatingSystem, DateTimeOffset now)
    {
        Name = string.IsNullOrWhiteSpace(name) ? "unnamed" : name.Trim()[..Math.Min(name.Trim().Length, 200)];
        OperatingSystem = string.IsNullOrWhiteSpace(operatingSystem) ? "unknown" : operatingSystem.Trim().ToLowerInvariant();
        Seen(now);
    }

    /// <summary>Records that the machine sent content.</summary>
    public void Seen(DateTimeOffset now)
    {
        if (now > LastSeenAt)
            LastSeenAt = now;
    }
}
