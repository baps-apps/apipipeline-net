using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;

namespace ApiPipeline.NET.Observability;

/// <summary>
/// Shared <see cref="ActivitySource"/> and <see cref="Meter"/> for ApiPipeline.NET.
/// Use with OpenTelemetry to export traces and metrics (e.g. rate limit rejections, correlation ID).
/// </summary>
public static class ApiPipelineTelemetry
{
    /// <summary>
    /// Activity source name for ApiPipeline.NET. Add this to OpenTelemetry tracing to capture pipeline spans/tags.
    /// </summary>
    public const string ActivitySourceName = "ApiPipeline.NET";

    /// <summary>
    /// Meter name for ApiPipeline.NET. Add this to OpenTelemetry metrics to capture pipeline metrics.
    /// </summary>
    public const string MeterName = "ApiPipeline.NET";

    private static readonly string InstrumentationVersion =
        typeof(ApiPipelineTelemetry).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(ApiPipelineTelemetry).Assembly.GetName().Version?.ToString()
        ?? "0.0.0";

    /// <summary>
    /// Activity source for pipeline-related spans and tags (e.g. correlation ID on current activity).
    /// </summary>
    public static readonly ActivitySource ActivitySource = new(ActivitySourceName, InstrumentationVersion);

    /// <summary>
    /// Meter for pipeline metrics (e.g. rate limit rejections, deprecation headers).
    /// </summary>
    public static readonly Meter Meter = new(MeterName, InstrumentationVersion);

    /// <summary>
    /// Counter: number of requests rejected by rate limiting.
    /// </summary>
    public static readonly Counter<long> RateLimitRejectedCount = Meter.CreateCounter<long>(
        "apipipeline.ratelimit.rejected",
        description: "Number of requests rejected by rate limiting.");

    /// <summary>
    /// Counter: number of responses that included API deprecation headers.
    /// </summary>
    public static readonly Counter<long> DeprecationHeadersAddedCount = Meter.CreateCounter<long>(
        "apipipeline.deprecation.headers_added",
        description: "Number of responses that included API version deprecation headers.");

    /// <summary>
    /// Counter: number of correlation IDs processed (generated or propagated).
    /// </summary>
    public static readonly Counter<long> CorrelationIdProcessedCount = Meter.CreateCounter<long>(
        "apipipeline.correlation_id.processed",
        description: "Number of correlation IDs processed (generated or propagated from client).");

    /// <summary>
    /// Counter: number of responses with security headers applied.
    /// </summary>
    public static readonly Counter<long> SecurityHeadersAppliedCount = Meter.CreateCounter<long>(
        "apipipeline.security_headers.applied",
        description: "Number of responses with security headers applied.");

    /// <summary>
    /// Counter: number of unhandled exceptions caught by the pipeline exception handler.
    /// </summary>
    public static readonly Counter<long> ExceptionHandledCount = Meter.CreateCounter<long>(
        "apipipeline.exceptions.handled",
        description: "Number of unhandled exceptions caught by the pipeline exception handler.");

    /// <summary>
    /// Sets the correlation ID on the current <see cref="Activity"/> so it appears in OpenTelemetry traces when exported.
    /// </summary>
    public static void SetCorrelationIdOnCurrentActivity(string correlationId)
    {
        if (string.IsNullOrEmpty(correlationId))
        {
            return;
        }

        Activity.Current?.SetTag("correlation_id", correlationId);
    }

    /// <summary>
    /// Records that a request was rejected by rate limiting. Call from the rate limiter's OnRejected callback.
    /// </summary>
    public static void RecordRateLimitRejected() => RateLimitRejectedCount.Add(1);

    /// <summary>
    /// Records that deprecation headers were added to a response.
    /// </summary>
    /// <param name="apiVersion">Optional API version string for the deprecated version (metric dimension).</param>
    public static void RecordDeprecationHeadersAdded(string? apiVersion = null)
    {
        if (apiVersion is { Length: > 0 })
        {
            DeprecationHeadersAddedCount.Add(1, new KeyValuePair<string, object?>("apipipeline.api_version", apiVersion));
        }
        else
        {
            DeprecationHeadersAddedCount.Add(1);
        }
    }

    /// <summary>
    /// Records that a correlation ID was processed (generated or propagated).
    /// </summary>
    public static void RecordCorrelationIdProcessed() => CorrelationIdProcessedCount.Add(1);

    /// <summary>
    /// Records that security headers were applied to a response.
    /// </summary>
    public static void RecordSecurityHeadersApplied() => SecurityHeadersAppliedCount.Add(1);

    /// <summary>
    /// Records that an unhandled exception was caught by the pipeline exception handler.
    /// </summary>
    public static void RecordExceptionHandled() => ExceptionHandledCount.Add(1);
}
