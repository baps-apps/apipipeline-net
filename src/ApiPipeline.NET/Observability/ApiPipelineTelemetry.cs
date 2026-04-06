using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;

namespace ApiPipeline.NET.Observability;

/// <summary>
/// Shared <see cref="ActivitySource"/> and <see cref="Meter"/> for ApiPipeline.NET.
/// Use with OpenTelemetry to export traces and metrics.
/// </summary>
public static class ApiPipelineTelemetry
{
    /// <summary>Activity source name for ApiPipeline.NET tracing.</summary>
    public const string ActivitySourceName = "ApiPipeline.NET";

    /// <summary>Meter name for ApiPipeline.NET metrics.</summary>
    public const string MeterName = "ApiPipeline.NET";

    private static readonly string InstrumentationVersion =
        typeof(ApiPipelineTelemetry).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(ApiPipelineTelemetry).Assembly.GetName().Version?.ToString()
        ?? "0.0.0";

    /// <summary>Activity source for pipeline-related spans and tags.</summary>
    public static readonly ActivitySource ActivitySource = new(ActivitySourceName, InstrumentationVersion);

    /// <summary>Meter for pipeline metrics.</summary>
    public static readonly Meter Meter = new(MeterName, InstrumentationVersion);

    /// <summary>
    /// Counter: requests rejected by rate limiting.
    /// Dimensions: <c>policy_name</c> (string), <c>partition_type</c> (user|ip|anonymous|unknown).
    /// </summary>
    public static readonly Counter<long> RateLimitRejectedCount = Meter.CreateCounter<long>(
        "apipipeline.ratelimit.rejected",
        description: "Number of requests rejected by rate limiting.");

    /// <summary>Counter: responses that included API version deprecation headers.</summary>
    public static readonly Counter<long> DeprecationHeadersAddedCount = Meter.CreateCounter<long>(
        "apipipeline.deprecation.headers_added",
        description: "Number of responses that included API version deprecation headers.");

    /// <summary>Counter: correlation IDs processed (generated or propagated).</summary>
    public static readonly Counter<long> CorrelationIdProcessedCount = Meter.CreateCounter<long>(
        "apipipeline.correlation_id.processed",
        description: "Number of correlation IDs processed (generated or propagated from client).");

    /// <summary>Counter: unhandled exceptions caught by the pipeline exception handler.</summary>
    public static readonly Counter<long> ExceptionHandledCount = Meter.CreateCounter<long>(
        "apipipeline.exceptions.handled",
        description: "Number of unhandled exceptions caught by the pipeline exception handler.");

    /// <summary>Counter: CORS requests rejected (origin not in AllowedOrigins).</summary>
    public static readonly Counter<long> CorsRejectedCount = Meter.CreateCounter<long>(
        "apipipeline.cors.rejected",
        description: "Number of CORS requests rejected due to disallowed origin.");

    /// <summary>Histogram: incoming request body size in bytes (sampled from Content-Length header).</summary>
    public static readonly Histogram<long> RequestBodyBytesHistogram = Meter.CreateHistogram<long>(
        "apipipeline.request.body_bytes",
        unit: "bytes",
        description: "Size of incoming request bodies (sampled from Content-Length when present).");

    /// <summary>
    /// Sets the correlation ID on the current <see cref="Activity"/>.
    /// </summary>
    public static void SetCorrelationIdOnCurrentActivity(string correlationId)
    {
        if (string.IsNullOrEmpty(correlationId)) return;
        Activity.Current?.SetTag("correlation_id", correlationId);
    }

    /// <summary>
    /// Records that a request was rejected by rate limiting.
    /// </summary>
    /// <param name="policyName">The name of the rate limiting policy that rejected the request.</param>
    /// <param name="partitionType">The partition type: "user", "ip", "anonymous", or "unknown".</param>
    public static void RecordRateLimitRejected(string? policyName = null, string? partitionType = null)
    {
        if (policyName is { Length: > 0 } && partitionType is { Length: > 0 })
        {
            RateLimitRejectedCount.Add(1,
                new KeyValuePair<string, object?>("policy_name", policyName),
                new KeyValuePair<string, object?>("partition_type", partitionType));
        }
        else
        {
            RateLimitRejectedCount.Add(1);
        }
    }

    /// <summary>Records that deprecation headers were added to a response.</summary>
    public static void RecordDeprecationHeadersAdded(string? apiVersion = null)
    {
        if (apiVersion is { Length: > 0 })
        {
            DeprecationHeadersAddedCount.Add(1,
                new KeyValuePair<string, object?>("apipipeline.api_version", apiVersion));
        }
        else
        {
            DeprecationHeadersAddedCount.Add(1);
        }
    }

    /// <summary>Records that a correlation ID was processed.</summary>
    public static void RecordCorrelationIdProcessed() => CorrelationIdProcessedCount.Add(1);

    /// <summary>Records that an unhandled exception was caught by the pipeline exception handler.</summary>
    public static void RecordExceptionHandled() => ExceptionHandledCount.Add(1);

    /// <summary>Records that a CORS request was rejected due to disallowed origin.</summary>
    public static void RecordCorsRejected() => CorsRejectedCount.Add(1);

    /// <summary>Records the size of an incoming request body (from Content-Length header).</summary>
    public static void RecordRequestBodyBytes(long bytes) => RequestBodyBytesHistogram.Record(bytes);
}
