using Microsoft.Extensions.DependencyInjection;
using Protostar.Registry.Api.Common;

namespace Protostar.Registry.Api.Infrastructure;

/// <summary>
/// Resolves and invokes the registered handlers for each domain event from the current scope. A small
/// per-event-type shim bridges the statically-typed handler interface to the runtime event type.
/// </summary>
public sealed class DomainEventDispatcher : IDomainEventDispatcher
{
    private readonly IServiceProvider _provider;

    public DomainEventDispatcher(IServiceProvider provider) => _provider = provider;

    public async Task DispatchAsync(IReadOnlyCollection<IDomainEvent> domainEvents, CancellationToken cancellationToken)
    {
        foreach (var domainEvent in domainEvents)
        {
            var shim = (Shim)Activator.CreateInstance(typeof(Shim<>).MakeGenericType(domainEvent.GetType()))!;
            await shim.DispatchAsync(_provider, domainEvent, cancellationToken);
        }
    }

    private abstract class Shim
    {
        public abstract Task DispatchAsync(IServiceProvider provider, IDomainEvent domainEvent, CancellationToken cancellationToken);
    }

    private sealed class Shim<TEvent> : Shim where TEvent : IDomainEvent
    {
        public override async Task DispatchAsync(IServiceProvider provider, IDomainEvent domainEvent, CancellationToken cancellationToken)
        {
            foreach (var handler in provider.GetServices<IDomainEventHandler<TEvent>>())
                await handler.HandleAsync((TEvent)domainEvent, cancellationToken);
        }
    }
}
