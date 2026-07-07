namespace Protostar.Registry.Api.Common;

/// <summary>
/// Marker for something that happened in the domain worth telling the rest of the system about. Raised
/// by an <see cref="AggregateRoot"/> during a unit of work and dispatched when it is saved.
/// </summary>
public interface IDomainEvent;
