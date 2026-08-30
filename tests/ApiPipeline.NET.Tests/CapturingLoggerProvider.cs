using Microsoft.Extensions.Logging;

namespace ApiPipeline.NET.Tests;

/// <summary>
/// Captures log records so a warning can be asserted on rather than eyeballed. Startup warnings are
/// part of this package's contract with operators — several of them are the only signal that a
/// configuration is doing nothing — so they are tested like any other behaviour.
/// </summary>
internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    /// <summary>Formatted messages, in the order they were logged.</summary>
    public List<string> Messages { get; } = [];

    /// <summary>Returns a logger that appends every record to <see cref="Messages"/>.</summary>
    public ILogger CreateLogger(string categoryName) => new Capturing(Messages);

    /// <summary>No unmanaged state to release.</summary>
    public void Dispose()
    {
    }

    private sealed class Capturing(List<string> sink) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (sink)
            {
                sink.Add(formatter(state, exception));
            }
        }
    }
}
