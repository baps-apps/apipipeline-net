namespace ApiPipeline.NET.RateLimiting.Redis.Tests;

/// <summary>
/// Captures log records so a warning can be asserted on rather than eyeballed. Records the
/// formatted message and, when present, the exception's full text, so a test can prove a secret
/// appears in neither.
/// </summary>
internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    /// <summary>Formatted messages (plus exception text), in the order they were logged.</summary>
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
            var message = formatter(state, exception);
            if (exception is not null)
            {
                message += Environment.NewLine + exception;
            }

            lock (sink)
            {
                sink.Add(message);
            }
        }
    }
}
