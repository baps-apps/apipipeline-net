using ApiPipeline.NET.Pipeline;
using FluentAssertions;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Memory;
using Microsoft.Extensions.Logging;

namespace ApiPipeline.NET.Tests;

/// <summary>
/// Behaviour that only exists because of an implementation detail, and would disappear silently if
/// that detail were simplified away: the log scope the correlation middleware opens.
/// </summary>
public sealed class CorrelationLogScopeTests
{
    private sealed class ScopeCapturingProvider : ILoggerProvider
    {
        public List<KeyValuePair<string, object?>> Scopes { get; } = [];

        public ILogger CreateLogger(string categoryName) => new Capturing(Scopes);

        public void Dispose()
        {
        }

        private sealed class Capturing(List<KeyValuePair<string, object?>> sink) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull
            {
                if (state is IEnumerable<KeyValuePair<string, object?>> pairs)
                {
                    lock (sink)
                    {
                        sink.AddRange(pairs);
                    }
                }

                return null;
            }

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
            }
        }
    }

    /// <summary>
    /// Every log line written while handling a request carries the correlation id. That is the whole
    /// reason an operator can find a request from an id a caller quotes, and it depends on the
    /// middleware's scope state being enumerable as key/value pairs.
    /// </summary>
    [Fact]
    public async Task CorrelationMiddleware_OpensLogScope_WithTheCorrelationId()
    {
        var capture = new ScopeCapturingProvider();
        await using var app = await TestAppBuilder.CreateAppAsync(
            TestAppBuilder.MinimalConfig(),
            configureServices: services => services.AddSingleton<ILoggerProvider>(capture));
        app.UseApiPipeline(pipeline => pipeline.WithCorrelationId());
        app.MapGet("/test", () => Results.Ok("ok"));
        await app.StartAsync(TestContext.Current.CancellationToken);

        var request = new HttpRequestMessage(HttpMethod.Get, "/test");
        request.Headers.TryAddWithoutValidation("X-Correlation-Id", "abc123");
        await app.GetTestClient().SendAsync(request, TestContext.Current.CancellationToken);

        lock (capture.Scopes)
        {
            capture.Scopes.Should().Contain(p => p.Key == "CorrelationId" && (string?)p.Value == "abc123");
        }
    }

    /// <summary>
    /// The host's activity tracking already puts <c>TraceId</c> on every scope; the middleware adding
    /// it again gave a log line two <c>TraceId</c> attributes wherever scopes are flattened
    /// (OpenTelemetry's <c>IncludeScopes</c>, Serilog). One, and it is the request's trace id.
    /// </summary>
    [Fact]
    public async Task LogLineInARequest_CarriesTraceIdOnce()
    {
        var capture = new FlattenedScopeProvider();
        await using var app = await TestAppBuilder.CreateAppAsync(
            TestAppBuilder.MinimalConfig(),
            configureServices: services => services.AddSingleton<ILoggerProvider>(capture));
        app.UseApiPipeline(pipeline => pipeline.WithCorrelationId());
        app.MapGet("/test", (ILoggerFactory loggers) =>
        {
            loggers.CreateLogger("Endpoint").LogInformation("handled");
            return Results.Ok(System.Diagnostics.Activity.Current?.TraceId.ToString());
        });
        await app.StartAsync(TestContext.Current.CancellationToken);

        var response = await app.GetTestClient().GetAsync("/test", TestContext.Current.CancellationToken);
        var traceId = System.Text.Json.JsonSerializer.Deserialize<string>(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        lock (capture.Lines)
        {
            var line = capture.Lines.Should().ContainSingle(l => l.Message == "handled").Subject;
            line.Properties.Where(p => p.Key == "TraceId").Should().ContainSingle()
                .Which.Value?.ToString().Should().Be(traceId);
            line.Properties.Should().Contain(p => p.Key == "CorrelationId");
        }
    }

    /// <summary>For each log line, every scope property in effect, flattened into one list.</summary>
    private sealed class FlattenedScopeProvider : ILoggerProvider, ISupportExternalScope
    {
        private IExternalScopeProvider _scopes = new LoggerExternalScopeProvider();

        public List<(string Message, List<KeyValuePair<string, object?>> Properties)> Lines { get; } = [];

        public ILogger CreateLogger(string categoryName) => new Capturing(this);

        public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;

        public void Dispose()
        {
        }

        private sealed class Capturing(FlattenedScopeProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => owner._scopes.Push(state);

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                var properties = new List<KeyValuePair<string, object?>>();
                owner._scopes.ForEachScope(
                    static (scope, list) =>
                    {
                        if (scope is IEnumerable<KeyValuePair<string, object?>> pairs)
                        {
                            list.AddRange(pairs);
                        }
                    },
                    properties);

                lock (owner.Lines)
                {
                    owner.Lines.Add((formatter(state, exception), properties));
                }
            }
        }
    }

    /// <summary>
    /// Records, for each error logged with an exception, the scope values active at that moment.
    /// </summary>
    private sealed class ErrorScopeProvider : ILoggerProvider, ISupportExternalScope
    {
        private IExternalScopeProvider _scopes = new LoggerExternalScopeProvider();

        public List<Dictionary<string, object?>> Errors { get; } = [];

        public ILogger CreateLogger(string categoryName) => new Capturing(this);

        public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;

        public void Dispose()
        {
        }

        private sealed class Capturing(ErrorScopeProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => owner._scopes.Push(state);

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (logLevel < LogLevel.Error || exception is null)
                {
                    return;
                }

                var values = new Dictionary<string, object?>();
                owner._scopes.ForEachScope(
                    static (scope, sink) =>
                    {
                        if (scope is IEnumerable<KeyValuePair<string, object?>> pairs)
                        {
                            foreach (var pair in pairs)
                            {
                                sink[pair.Key] = pair.Value;
                            }
                        }
                    },
                    values);

                lock (owner.Errors)
                {
                    owner.Errors.Add(values);
                }
            }
        }
    }

    private static async Task<List<Dictionary<string, object?>>> ErrorScopesAsync(bool handlerInsidePipeline)
    {
        var capture = new ErrorScopeProvider();
        await using var app = await TestAppBuilder.CreateAppAsync(
            TestAppBuilder.MinimalConfig(),
            addProblemDetails: true,
            configureServices: services => services.AddSingleton<ILoggerProvider>(capture));

        if (handlerInsidePipeline)
        {
            app.UseApiPipeline(pipeline => pipeline
                .WithCorrelationId()
                .WithMiddleware(a => a.UseConsumerErrorHandling()));
        }
        else
        {
            app.UseConsumerErrorHandling();
            app.UseApiPipeline(pipeline => pipeline.WithCorrelationId());
        }

        app.MapGet("/boom", string () => throw new InvalidOperationException("boom"));
        await app.StartAsync(TestContext.Current.CancellationToken);

        var request = new HttpRequestMessage(HttpMethod.Get, "/boom");
        request.Headers.TryAddWithoutValidation("X-Correlation-Id", "abc123");
        var response = await app.GetTestClient().SendAsync(request, TestContext.Current.CancellationToken);
        ((int)response.StatusCode).Should().Be(500);

        lock (capture.Errors)
        {
            return [.. capture.Errors];
        }
    }

    /// <summary>
    /// The unhandled-exception log line is the one an operator looks for from an id a caller
    /// quotes. With the exception handler inside the pipeline, after <c>WithCorrelationId()</c> (as
    /// the README documents), it is written inside the correlation scope and carries the id.
    /// </summary>
    [Fact]
    public async Task ExceptionHandlerInsideThePipeline_LogsTheUnhandledException_WithTheCorrelationId()
    {
        var errors = await ErrorScopesAsync(handlerInsidePipeline: true);

        errors.Should().NotBeEmpty();
        errors.Should().OnlyContain(scope => scope.ContainsKey("CorrelationId") && (string?)scope["CorrelationId"] == "abc123");
    }

    /// <summary>
    /// Why the README places it there: a logging scope is held in an <c>AsyncLocal</c>, so an
    /// exception handler outside the correlation middleware logs after the scope has unwound, and
    /// that line has no correlation id. Pinned so the documented placement keeps its reason.
    /// </summary>
    [Fact]
    public async Task ExceptionHandlerOutsideThePipeline_LogsWithoutTheCorrelationId()
    {
        var errors = await ErrorScopesAsync(handlerInsidePipeline: false);

        errors.Should().NotBeEmpty();
        errors.Should().OnlyContain(scope => !scope.ContainsKey("CorrelationId"));
    }
}
