using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Protostar.Registry.Acceptance.Support;

/// <summary>Every log entry one scenario's registry writes at or above the host's configured level.</summary>
/// <remarks>
/// Added to the host as one more logger provider, so it sees exactly what the console would: the host's level filters
/// (<see cref="RegistryFactory"/> sets Warning) apply to it as to any provider. It keeps entries in memory for the
/// scenario's assertions, such as "the first start logged no errors".
/// </remarks>
public sealed class CapturedLogs : ILoggerProvider
{
    private readonly ConcurrentQueue<CapturedLogEntry> _entries = new();

    /// <summary>The entries written so far, oldest first.</summary>
    public IReadOnlyList<CapturedLogEntry> Entries => _entries.ToArray();

    public ILogger CreateLogger(string categoryName) => new Logger(categoryName, _entries);

    public void Dispose()
    {
    }

    private sealed class Logger : ILogger
    {
        private readonly string _category;
        private readonly ConcurrentQueue<CapturedLogEntry> _entries;

        public Logger(string category, ConcurrentQueue<CapturedLogEntry> entries)
        {
            _category = category;
            _entries = entries;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            _entries.Enqueue(new CapturedLogEntry(logLevel, _category, eventId, formatter(state, exception), exception));
    }
}

/// <summary>One log entry the registry wrote.</summary>
public sealed record CapturedLogEntry(LogLevel Level, string Category, EventId EventId, string Message, Exception? Exception)
{
    public override string ToString() => $"{Level} {Category} [{EventId.Id} {EventId.Name}]: {Message}";
}
