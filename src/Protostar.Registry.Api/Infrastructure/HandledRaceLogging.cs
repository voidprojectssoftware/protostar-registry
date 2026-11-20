using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Npgsql;

namespace Protostar.Registry.Api.Infrastructure;

/// <summary>Logs EF's failed-command and failed-save events itself, at Debug for a handled insert race and at Error otherwise.</summary>
/// <remarks>
/// <para>
/// EF writes <see cref="RelationalEventId.CommandError"/> and <see cref="CoreEventId.SaveChangesFailed"/> at
/// Error before the exception reaches the caller, so a race the caller recovers from (two requests inserting the
/// same key, the loser re-reading) would read as an incident. An interceptor cannot change the level of a log EF
/// has already written, so <see cref="HandledRaceLoggingExtensions.LogHandledRacesAtDebug"/> stops EF writing those two events and this
/// interceptor writes them instead, with EF's own text, event id and exception.
/// </para>
/// <para>
/// Only a unique violation (SqlState 23505) inside <see cref="RegistryDbContext.SaveChangesRacingAsync"/> drops
/// to Debug. Every other failure, including a unique violation in an ordinary save, keeps Error.
/// </para>
/// </remarks>
public sealed class HandledRaceLogging : DbCommandInterceptor, ISaveChangesInterceptor
{
    public override void CommandFailed(DbCommand command, CommandErrorEventData eventData) =>
        Log<DbLoggerCategory.Database.Command>(eventData, eventData.Exception);

    public override Task CommandFailedAsync(DbCommand command, CommandErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        Log<DbLoggerCategory.Database.Command>(eventData, eventData.Exception);
        return Task.CompletedTask;
    }

    public void SaveChangesFailed(DbContextErrorEventData eventData) =>
        Log<DbLoggerCategory.Update>(eventData, eventData.Exception);

    public Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        Log<DbLoggerCategory.Update>(eventData, eventData.Exception);
        return Task.CompletedTask;
    }

    private static void Log<TCategory>(DbContextEventData eventData, Exception exception)
        where TCategory : LoggerCategory<TCategory>, new()
    {
        if (eventData.Context is not { } context)
            return;

        var level = context is RegistryDbContext { SavingRacingInsert: true } && IsUniqueViolation(exception)
            ? LogLevel.Debug
            : LogLevel.Error;
        var logger = context.GetService<IDiagnosticsLogger<TCategory>>().Logger;
        logger.Log(level, eventData.EventId, eventData.ToString(), exception, static (message, _) => message);
    }

    // A failed command carries the PostgresException itself; a failed save wraps it in a DbUpdateException.
    private static bool IsUniqueViolation(Exception exception) =>
        (exception as PostgresException ?? exception.InnerException as PostgresException)?.SqlState == PostgresErrorCodes.UniqueViolation;
}

/// <summary>Wires <see cref="HandledRaceLogging"/> into the context options.</summary>
public static class HandledRaceLoggingExtensions
{
    /// <summary>Replaces EF's Error logs for failed commands and saves with <see cref="HandledRaceLogging"/>.</summary>
    /// <remarks>The two calls belong together: ignoring the events without the interceptor would silence real errors.</remarks>
    public static DbContextOptionsBuilder LogHandledRacesAtDebug(this DbContextOptionsBuilder options) =>
        options
            .AddInterceptors(new HandledRaceLogging())
            .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.CommandError, CoreEventId.SaveChangesFailed));
}
