using Protostar.Registry.Api.Sessions;
using Xunit;

namespace Protostar.Registry.Tests.Sessions.Domain;

/// <summary>Contract tests for <see cref="Machine"/>.</summary>
public sealed class MachineTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 19, 10, 0, 0, TimeSpan.Zero);

    private static Machine NewMachine() => Machine.Register(Guid.NewGuid(), Guid.NewGuid(), "box", "linux", Now);

    [Fact]
    public void A_new_machine_was_last_seen_when_it_registered()
    {
        var machine = NewMachine();

        Assert.Equal(Now, machine.RegisteredAt);
        Assert.Equal(Now, machine.LastSeenAt);
    }

    [Fact]
    public void A_registration_without_an_id_is_refused()
    {
        Assert.Throws<ArgumentException>(() => Machine.Register(Guid.Empty, Guid.NewGuid(), "box", "linux", Now));
    }

    [Fact]
    public void A_later_sighting_moves_last_seen_forward()
    {
        var machine = NewMachine();

        machine.Seen(Now.AddMinutes(5));

        Assert.Equal(Now.AddMinutes(5), machine.LastSeenAt);
    }

    [Fact]
    public void An_earlier_sighting_does_not_move_last_seen_backwards()
    {
        var machine = NewMachine();
        machine.Seen(Now.AddMinutes(5));

        machine.Seen(Now.AddMinutes(1));

        Assert.Equal(Now.AddMinutes(5), machine.LastSeenAt);
    }

    [Fact]
    public void An_update_with_an_earlier_clock_reading_keeps_the_new_details_but_not_the_earlier_time()
    {
        var machine = NewMachine();

        machine.Update("renamed", "Windows", Now.AddSeconds(-1));

        Assert.Equal("renamed", machine.Name);
        Assert.Equal("windows", machine.OperatingSystem);
        Assert.Equal(Now, machine.LastSeenAt);
    }

    [Fact]
    public void A_blank_name_and_os_are_stored_as_unnamed_and_unknown()
    {
        var machine = Machine.Register(Guid.NewGuid(), Guid.NewGuid(), "  ", "", Now);

        Assert.Equal("unnamed", machine.Name);
        Assert.Equal("unknown", machine.OperatingSystem);
    }
}
