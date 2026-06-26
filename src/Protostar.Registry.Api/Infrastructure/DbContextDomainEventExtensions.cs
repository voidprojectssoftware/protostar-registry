using Microsoft.EntityFrameworkCore;
using Protostar.Registry.Api.Common;

namespace Protostar.Registry.Api.Infrastructure;

/// <summary>
/// Saves the unit of work, then dispatches the domain events the tracked aggregates raised. Dispatch runs
/// after the commit so handlers only see persisted facts, and events are cleared before dispatch so a
/// handler that saves again cannot re-trigger them.
/// </summary>
/// <remarks>
/// This lives at the call site rather than in an overridden <c>SaveChangesAsync</c> because Aspire pools
/// the <see cref="RegistryDbContext"/>, and a pooled context cannot take a scoped dispatcher in its
/// constructor.
/// </remarks>
public static class DbContextDomainEventExtensions
{
    public static async Task<int> SaveChangesAndDispatchAsync(
        this DbContext db, IDomainEventDispatcher dispatcher, CancellationToken cancellationToken)
    {
        var result = await db.SaveChangesAsync(cancellationToken);

        var aggregates = db.ChangeTracker.Entries<AggregateRoot>()
            .Select(entry => entry.Entity)
            .Where(aggregate => aggregate.DomainEvents.Count > 0)
            .ToList();

        var domainEvents = aggregates.SelectMany(aggregate => aggregate.DomainEvents).ToList();
        foreach (var aggregate in aggregates)
            aggregate.ClearDomainEvents();

        if (domainEvents.Count > 0)
            await dispatcher.DispatchAsync(domainEvents, cancellationToken);

        return result;
    }
}
