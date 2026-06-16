namespace Protostar.Registry.Api.Common;

/// <summary>Dispatches raised domain events to their registered <see cref="IDomainEventHandler{TEvent}"/>s.</summary>
public interface IDomainEventDispatcher
{
    Task DispatchAsync(IReadOnlyCollection<IDomainEvent> domainEvents, CancellationToken cancellationToken);
}
