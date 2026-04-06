using System.Diagnostics;
using System.Text.RegularExpressions;
using ApiPipeline.NET.Observability;

namespace ApiPipeline.NET.Middleware;

/// <summary>
/// ASP.NET Core middleware that ensures every request and response carries a correlation ID.
/// Incoming correlation IDs are validated against a strict alphanumeric pattern to prevent header injection.
/// </summary>
public sealed partial class CorrelationIdMiddleware
{
    /// <summary>
    /// The HTTP header name used to propagate the correlation identifier.
    /// </summary>
    public const string HeaderName = "X-Correlation-Id";

    private readonly RequestDelegate _next;
    private readonly ILogger<CorrelationIdMiddleware> _logger;

    [GeneratedRegex(@"^[a-zA-Z0-9\-_.]{1,128}$")]
    private static partial Regex SafeCorrelationIdPattern();

    /// <summary>
    /// Initializes a new instance of the <see cref="CorrelationIdMiddleware"/> class.
    /// </summary>
    /// <param name="next">The next middleware in the ASP.NET Core pipeline.</param>
    /// <param name="logger">Logger for diagnostics.</param>
    public CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    /// <summary>
    /// Executes the middleware, attaching a validated correlation ID to the current HTTP context.
    /// Invalid or missing incoming correlation IDs are replaced with a server-generated value.
    /// </summary>
    /// <param name="context">The current HTTP context.</param>
    /// <returns>A task that represents the completion of request processing.</returns>
    public async Task Invoke(HttpContext context)
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

        using (_logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId }))
        {
            await _next(context);
        }
    }
}
