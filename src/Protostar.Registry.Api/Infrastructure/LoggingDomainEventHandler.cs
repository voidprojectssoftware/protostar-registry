using Microsoft.Extensions.Logging;
using Protostar.Registry.Api.Common;

namespace Protostar.Registry.Api.Infrastructure;

/// <summary>
/// Logs every domain event. The placeholder consumer until real handlers exist; registered as an open
/// generic so it handles every event type. Remove or keep alongside real handlers as features land.
/// </summary>
public sealed class LoggingDomainEventHandler<TEvent> : IDomainEventHandler<TEvent> where TEvent : IDomainEvent
{
    private readonly ILogger<LoggingDomainEventHandler<TEvent>> _logger;

    public LoggingDomainEventHandler(ILogger<LoggingDomainEventHandler<TEvent>> logger) => _logger = logger;

    public Task HandleAsync(TEvent domainEvent, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Domain event raised: {DomainEvent}", domainEvent);
        return Task.CompletedTask;
    }
}
