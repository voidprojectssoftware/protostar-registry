using Protostar.Registry.Api.Common;

namespace Protostar.Registry.Tests.Common;

public sealed class AggregateRootTests
{
    private sealed record TestEvent(int Id) : IDomainEvent;

    private sealed class TestAggregate : AggregateRoot
    {
        public void Raise(IDomainEvent e) => RaiseDomainEvent(e); // expose the protected method for testing
    }

    [Fact]
    public void DomainEvents_is_empty_for_a_new_aggregate()
    {
        var aggregate = new TestAggregate();

        Assert.Empty(aggregate.DomainEvents);
    }

    [Fact]
    public void Raising_an_event_adds_it_to_DomainEvents()
    {
        var aggregate = new TestAggregate();
        var domainEvent = new TestEvent(1);

        aggregate.Raise(domainEvent);

        Assert.Contains(domainEvent, aggregate.DomainEvents);
    }

    [Fact]
    public void Raising_an_event_results_in_a_single_entry()
    {
        var aggregate = new TestAggregate();

        aggregate.Raise(new TestEvent(1));

        Assert.Single(aggregate.DomainEvents);
    }

    [Fact]
    public void Raising_several_events_preserves_their_order()
    {
        var aggregate = new TestAggregate();
        var first = new TestEvent(1);
        var second = new TestEvent(2);
        var third = new TestEvent(3);

        aggregate.Raise(first);
        aggregate.Raise(second);
        aggregate.Raise(third);

        Assert.Equal(new IDomainEvent[] { first, second, third }, aggregate.DomainEvents);
    }

    [Fact]
    // ASSUMES: DomainEvents is a list "in order", not a set, so the same instance raised twice yields two entries (no de-duplication documented).
    public void Raising_the_same_event_instance_twice_records_two_entries()
    {
        var aggregate = new TestAggregate();
        var domainEvent = new TestEvent(1);

        aggregate.Raise(domainEvent);
        aggregate.Raise(domainEvent);

        Assert.Equal(new IDomainEvent[] { domainEvent, domainEvent }, aggregate.DomainEvents);
    }

    [Fact]
    public void ClearDomainEvents_empties_the_collection()
    {
        var aggregate = new TestAggregate();
        aggregate.Raise(new TestEvent(1));
        aggregate.Raise(new TestEvent(2));

        aggregate.ClearDomainEvents();

        Assert.Empty(aggregate.DomainEvents);
    }

    [Fact]
    public void ClearDomainEvents_on_a_new_aggregate_leaves_it_empty()
    {
        var aggregate = new TestAggregate();

        aggregate.ClearDomainEvents();

        Assert.Empty(aggregate.DomainEvents);
    }

    [Fact]
    public void Raising_after_ClearDomainEvents_records_only_the_new_event()
    {
        var aggregate = new TestAggregate();
        aggregate.Raise(new TestEvent(1));
        aggregate.ClearDomainEvents();
        var afterClear = new TestEvent(2);

        aggregate.Raise(afterClear);

        Assert.Equal(new IDomainEvent[] { afterClear }, aggregate.DomainEvents);
    }
}
