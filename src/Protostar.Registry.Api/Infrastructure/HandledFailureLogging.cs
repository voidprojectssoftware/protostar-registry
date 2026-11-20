using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Protostar.Registry.Api.Infrastructure;

/// <summary>Logs EF's failed-command and failed-save events itself, at Debug for the failures the caller handles and at Error otherwise.</summary>
/// <remarks>
/// <para>
/// EF writes <see cref="RelationalEventId.CommandError"/> and <see cref="CoreEventId.SaveChangesFailed"/> at
/// Error before the exception reaches the caller, so a failure the caller recovers from would read as an incident.
/// An interceptor cannot change the level of a log EF has already written, so
/// <see cref="HandledFailureLoggingExtensions.LogHandledFailuresAtDebug"/> stops EF writing those two events and this
/// interceptor writes them instead, with EF's own text, event id and exception.
/// </para>
/// <para>
/// Two failures drop to Debug, and every other failure keeps Error:
/// </para>
/// <list type="bullet">
/// <item>A unique violation (SqlState 23505) inside <see cref="RegistryDbContext.SaveChangesRacingAsync"/>: a lost
/// insert race, which the caller recovers from by re-reading. A unique violation in an ordinary save keeps Error.</item>
/// <item>The migrator's read of the migrations history table failing with "undefined table" (SqlState 42P01). Npgsql
/// does not check whether the history table exists before reading it; it reads, catches 42P01 and treats the database
/// as having no migrations applied, then creates the table. So every first start on an empty database fails that one
/// command by design (registry#69). Any other failed migration command, including 42P01 from a migration's own SQL,
/// keeps Error.</item>
/// </list>
/// </remarks>
public sealed class HandledFailureLogging : DbCommandInterceptor, ISaveChangesInterceptor
{
    public override void CommandFailed(DbCommand command, CommandErrorEventData eventData) =>
        Log<DbLoggerCategory.Database.Command>(eventData, eventData.Exception, IsHandled(command, eventData));

    public override Task CommandFailedAsync(DbCommand command, CommandErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        Log<DbLoggerCategory.Database.Command>(eventData, eventData.Exception, IsHandled(command, eventData));
        return Task.CompletedTask;
    }

    public void SaveChangesFailed(DbContextErrorEventData eventData) =>
        Log<DbLoggerCategory.Update>(eventData, eventData.Exception, IsLostInsertRace(eventData.Context, eventData.Exception));

    public Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        Log<DbLoggerCategory.Update>(eventData, eventData.Exception, IsLostInsertRace(eventData.Context, eventData.Exception));
        return Task.CompletedTask;
    }

    private static void Log<TCategory>(DbContextEventData eventData, Exception exception, bool handled)
        where TCategory : LoggerCategory<TCategory>, new()
    {
        if (eventData.Context is not { } context)
            return;

        var logger = context.GetService<IDiagnosticsLogger<TCategory>>().Logger;
        logger.Log(handled ? LogLevel.Debug : LogLevel.Error, eventData.EventId, eventData.ToString(), exception, static (message, _) => message);
    }

    private static bool IsHandled(DbCommand command, CommandErrorEventData eventData) =>
        IsLostInsertRace(eventData.Context, eventData.Exception) || IsHistoryProbeOnEmptyDatabase(command, eventData);

    private static bool IsLostInsertRace(DbContext? context, Exception exception) =>
        context is RegistryDbContext { SavingRacingInsert: true } && SqlState(exception) == PostgresErrorCodes.UniqueViolation;

    // The probe runs as a reader, where a migration's own SQL runs as a non-query; the text check, a SELECT from the
    // history table, narrows it to the probe. A missing-table failure anywhere else in a migration keeps Error.
    private static bool IsHistoryProbeOnEmptyDatabase(DbCommand command, CommandErrorEventData eventData) =>
        eventData is { CommandSource: CommandSource.Migrations, ExecuteMethod: DbCommandMethod.ExecuteReader, Context: { } context }
        && SqlState(eventData.Exception) == PostgresErrorCodes.UndefinedTable
        && command.CommandText.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
        && command.CommandText.Contains(
            "FROM " + context.GetService<ISqlGenerationHelper>().DelimitIdentifier(HistoryRepository.DefaultTableName),
            StringComparison.Ordinal);

    // A failed command carries the PostgresException itself; a failed save wraps it in a DbUpdateException.
    private static string? SqlState(Exception exception) =>
        (exception as PostgresException ?? exception.InnerException as PostgresException)?.SqlState;
}

/// <summary>Wires <see cref="HandledFailureLogging"/> into the context options.</summary>
public static class HandledFailureLoggingExtensions
{
    /// <summary>Replaces EF's Error logs for failed commands and saves with <see cref="HandledFailureLogging"/>.</summary>
    /// <remarks>The two calls belong together: ignoring the events without the interceptor would silence real errors.</remarks>
    public static DbContextOptionsBuilder LogHandledFailuresAtDebug(this DbContextOptionsBuilder options) =>
        options
            .AddInterceptors(new HandledFailureLogging())
            .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.CommandError, CoreEventId.SaveChangesFailed));
}
