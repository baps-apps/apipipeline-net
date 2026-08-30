using System.Diagnostics.Metrics;
using System.Reflection;

namespace ApiPipeline.NET.Observability;

/// <summary>
/// The package's instruments, created from the host's <see cref="IMeterFactory"/>, one set per host.
/// Names are under <c>apipipeline.</c>; every attribute value is bounded (configuration or a fixed
/// set). See docs/OBSERVABILITY.md.
/// </summary>
internal sealed class ApiPipelineMetrics
{
    private static readonly string InstrumentationVersion =
        typeof(ApiPipelineMetrics).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(ApiPipelineMetrics).Assembly.GetName().Version?.ToString()
        ?? "0.0.0";

    private readonly Counter<long> _rateLimitRejected;
    private readonly Counter<long> _edgeRateLimitRejected;
    private readonly Counter<long> _rateLimitPartitionOverflow;
    private readonly Counter<long> _edgeRateLimitPartitionOverflow;
    private readonly Counter<long> _corsRejected;
    private readonly Counter<long> _correlationIdRejected;
    private readonly Histogram<long> _requestBodySize;

    public ApiPipelineMetrics(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);

        var meter = meterFactory.Create(new MeterOptions(ApiPipelineTelemetry.MeterName)
        {
            Version = InstrumentationVersion,
        });

        // Adds what aspnetcore.rate_limiting.requests lacks: partition type and default policy name.
        _rateLimitRejected = meter.CreateCounter<long>(
            "apipipeline.rate_limiting.rejected",
            unit: "{request}",
            description:
                "Number of requests rejected by rate limiting, by policy name (including the " +
                "default policy) and partition type.");

        // The edge limiter's only signal: it bypasses RateLimiterMiddleware.
        _edgeRateLimitRejected = meter.CreateCounter<long>(
            "apipipeline.edge_rate_limiting.rejected",
            unit: "{request}",
            description: "Number of requests shed by the pre-authentication edge rate limiter.");

        _rateLimitPartitionOverflow = meter.CreateCounter<long>(
            "apipipeline.rate_limiting.partitions.overflow",
            unit: "{request}",
            description:
                "Number of requests the rate limiter could not give a partition of their own because " +
                "MaxIpPartitions (partition_type ip) or MaxIdentityPartitions (partition_type identity) " +
                "partitions were already live.");

        _edgeRateLimitPartitionOverflow = meter.CreateCounter<long>(
            "apipipeline.edge_rate_limiting.partitions.overflow",
            unit: "{request}",
            description:
                "Number of requests the edge rate limiter sent to its shared overflow bucket because " +
                "MaxIpPartitions client-address partitions were already live.");

        _corsRejected = meter.CreateCounter<long>(
            "apipipeline.cors.rejected",
            unit: "{request}",
            description: "Number of CORS requests rejected due to a disallowed origin.");

        _correlationIdRejected = meter.CreateCounter<long>(
            "apipipeline.correlation_id.rejected",
            unit: "{request}",
            description:
                "Number of requests whose inbound X-Correlation-Id was rejected and replaced with a " +
                "generated id, so the caller's logs cannot be joined to this service's.");

        // Not http.server.request.body.size: still experimental, and the framework may emit it.
        _requestBodySize = meter.CreateHistogram(
            "apipipeline.request.body.size",
            unit: "By",
            description: "Size of incoming request bodies, from Content-Length when present.",
            tags: null,
            advice: new InstrumentAdvice<long> { HistogramBucketBoundaries = RequestBodySizeBuckets });
    }

    /// <summary>256 B, then powers of four from 1 KiB to 64 MiB (above Kestrel's 30 MB default).</summary>
    internal static readonly IReadOnlyList<long> RequestBodySizeBuckets =
    [
        256,
        1_024,             // 1 KiB
        4_096,             // 4 KiB
        16_384,            // 16 KiB
        65_536,            // 64 KiB
        262_144,           // 256 KiB
        1_048_576,         // 1 MiB
        4_194_304,         // 4 MiB
        16_777_216,        // 16 MiB
        67_108_864,        // 64 MiB
    ];

    /// <summary>A request rejected by the main limiter.</summary>
    /// <param name="policyName">The policy.</param>
    /// <param name="partitionType"><c>user</c>, <c>apikey</c>, <c>ip</c>, or <c>anonymous</c>.</param>
    public void RateLimitRejected(string? policyName, string? partitionType)
    {
        if (policyName is { Length: > 0 } && partitionType is { Length: > 0 })
        {
            _rateLimitRejected.Add(1,
                new KeyValuePair<string, object?>("apipipeline.rate_limiting.policy", policyName),
                new KeyValuePair<string, object?>("apipipeline.rate_limiting.partition_type", partitionType));
        }
        else
        {
            _rateLimitRejected.Add(1);
        }
    }

    /// <summary>A request shed by the edge limiter. Untagged: the only dimension would be the address.</summary>
    public void EdgeRateLimitRejected() => _edgeRateLimitRejected.Add(1);

    /// <summary>A new partition that did not fit under the main limiter's cap.</summary>
    /// <param name="partitionType"><c>ip</c> or <c>identity</c>.</param>
    public void RateLimitPartitionOverflow(string partitionType) =>
        _rateLimitPartitionOverflow.Add(1, new KeyValuePair<string, object?>("apipipeline.rate_limiting.partition_type", partitionType));

    public void EdgeRateLimitPartitionOverflow() => _edgeRateLimitPartitionOverflow.Add(1);

    /// <summary>A CORS request from an origin not allowed.</summary>
    public void CorsRejected() => _corsRejected.Add(1);

    /// <summary>An inbound correlation id replaced. Untagged: the value is caller-controlled.</summary>
    public void CorrelationIdRejected() => _correlationIdRejected.Add(1);

    /// <summary>An incoming request body size.</summary>
    /// <param name="bytes"><c>Content-Length</c>.</param>
    /// <param name="httpMethod">Normalized by <see cref="HttpMethodTag"/>.</param>
    /// <param name="route">The route template; <c>null</c> when no endpoint matched.</param>
    public void RequestBodySize(long bytes, string httpMethod, string? route)
    {
        if (route is { Length: > 0 })
        {
            _requestBodySize.Record(bytes,
                new KeyValuePair<string, object?>("http.request.method", httpMethod),
                new KeyValuePair<string, object?>("http.route", route));
        }
        else
        {
            _requestBodySize.Record(bytes,
                new KeyValuePair<string, object?>("http.request.method", httpMethod));
        }
    }
}
