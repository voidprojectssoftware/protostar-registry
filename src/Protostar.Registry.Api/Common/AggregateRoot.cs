using System.ComponentModel.DataAnnotations.Schema;

namespace Protostar.Registry.Api.Common;

/// <summary>
/// Base for aggregate roots: the consistency boundary other code loads, mutates through behavior, and
/// saves as a unit. Records <see cref="IDomainEvent"/>s raised while handling a command so the
/// persistence layer can dispatch them after the unit of work commits.
/// </summary>
public abstract class AggregateRoot
{
    private readonly List<IDomainEvent> _domainEvents = [];

    /// <summary>Events raised during the current unit of work, in order; cleared once dispatched.</summary>
    [NotMapped]
    public IReadOnlyCollection<IDomainEvent> DomainEvents => _domainEvents;

    protected void RaiseDomainEvent(IDomainEvent domainEvent) => _domainEvents.Add(domainEvent);

    public void ClearDomainEvents() => _domainEvents.Clear();
}
