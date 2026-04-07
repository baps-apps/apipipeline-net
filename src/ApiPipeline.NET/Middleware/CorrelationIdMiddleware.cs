using System.Diagnostics;
using System.Text.RegularExpressions;
using ApiPipeline.NET.Observability;

namespace ApiPipeline.NET.Middleware;

/// <summary>
/// ASP.NET Core middleware that ensures every request and response carries a correlation ID.
/// Incoming correlation IDs are validated against a strict alphanumeric pattern to prevent header injection.
/// Registered via <c>UseMiddleware&lt;CorrelationIdMiddleware&gt;()</c>
/// after calling <see cref="Extensions.ServiceCollectionExtensions.AddCorrelationId"/>.
/// </summary>
public sealed partial class CorrelationIdMiddleware : IMiddleware
{
    /// <summary>
    /// The HTTP header name used to propagate the correlation identifier.
    /// </summary>
    public const string HeaderName = "X-Correlation-Id";

    private readonly ILogger<CorrelationIdMiddleware> _logger;

    [GeneratedRegex(@"^[a-zA-Z0-9\-_.]{1,128}$")]
    private static partial Regex SafeCorrelationIdPattern();

    /// <summary>
    /// Initializes a new instance of the <see cref="CorrelationIdMiddleware"/> class.
    /// </summary>
    /// <param name="logger">Logger for diagnostics.</param>
    public CorrelationIdMiddleware(ILogger<CorrelationIdMiddleware> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Executes the middleware, attaching a validated correlation ID to the current HTTP context.
    /// Invalid or missing incoming correlation IDs are replaced with a server-generated value.
    /// </summary>
    /// <param name="context">The current HTTP context.</param>
    /// <param name="next">The next middleware delegate.</param>
    /// <returns>A task that represents the completion of request processing.</returns>
    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        string correlationId;

        if (context.Request.Headers.TryGetValue(HeaderName, out var existing)
            && !string.IsNullOrWhiteSpace(existing))
        {
            var incoming = existing.ToString();
            if (SafeCorrelationIdPattern().IsMatch(incoming))
            {
                correlationId = incoming;
            }
            else
            {
                correlationId = Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N");
                _logger.LogDebug(
                    "Rejected invalid correlation ID from request header, generated {CorrelationId}",
                    correlationId);
            }
        }
        else
        {
            correlationId = Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N");
        }

        context.Items[HeaderName] = correlationId;

        context.Response.OnStarting(static state =>
        {
            var (ctx, id) = ((HttpContext, string))state;
            ctx.Response.Headers[HeaderName] = id;
            return Task.CompletedTask;
        }, (context, correlationId));

        ApiPipelineTelemetry.SetCorrelationIdOnCurrentActivity(correlationId);
        ApiPipelineTelemetry.RecordCorrelationIdProcessed();

        using (_logger.BeginScope(new CorrelationScope(correlationId)))
        {
            await next(context);
        }
    }

    /// <summary>
    /// Lightweight value-type scope state that avoids per-request array allocation.
    /// Loggers that accept <see cref="IReadOnlyList{T}"/> of <see cref="KeyValuePair{TKey,TValue}"/>
    /// can enumerate the single correlation ID entry without heap allocation at the call site.
    /// </summary>
    private readonly struct CorrelationScope(string correlationId) : IReadOnlyList<KeyValuePair<string, object?>>
    {
        public int Count => 1;

        public KeyValuePair<string, object?> this[int index] => index == 0
            ? new("CorrelationId", correlationId)
            : throw new ArgumentOutOfRangeException(nameof(index));

        public Enumerator GetEnumerator() => new(correlationId);
        IEnumerator<KeyValuePair<string, object?>> IEnumerable<KeyValuePair<string, object?>>.GetEnumerator() => new Enumerator(correlationId);
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => new Enumerator(correlationId);

        public struct Enumerator(string correlationId) : IEnumerator<KeyValuePair<string, object?>>
        {
            private bool _moved;
            public readonly KeyValuePair<string, object?> Current => new("CorrelationId", correlationId);
            readonly object System.Collections.IEnumerator.Current => Current;
            public bool MoveNext() => !_moved && (_moved = true);
            public void Reset() => _moved = false;
            public readonly void Dispose() { }
        }
    }
}
