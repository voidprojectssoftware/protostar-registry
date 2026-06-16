namespace Protostar.Registry.Api.Common;

/// <summary>
/// Handles a domain event after the unit of work that raised it has committed. A feature reacts to an
/// event by registering an implementation; several handlers may handle the same event.
/// </summary>
public interface IDomainEventHandler<in TEvent> where TEvent : IDomainEvent
{
    Task HandleAsync(TEvent domainEvent, CancellationToken cancellationToken);
}
