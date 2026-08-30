using System.Diagnostics.Metrics;
using System.Reflection;
using ApiPipeline.NET.Observability;

namespace ApiPipeline.NET.RateLimiting.Redis;

/// <summary>
/// The add-on's instruments, on the core <c>ApiPipeline.NET</c> meter: <c>store.fallback</c>
/// (requests decided without Redis), <c>store.duration</c> (Redis call time) and
/// <c>store.degraded</c> (1 while the circuit is open). Names under
/// <c>apipipeline.rate_limiting.</c>; see docs/OBSERVABILITY.md.
/// </summary>
internal sealed class RedisRateLimitingMetrics
{
    // The core package's version, so IMeterFactory returns the core meter, not a second one.
    private static readonly string InstrumentationVersion =
        typeof(ApiPipelineTelemetry).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(ApiPipelineTelemetry).Assembly.GetName().Version?.ToString()
        ?? "0.0.0";

    /// <summary>ASP.NET Core's request-duration boundaries plus 0.5 ms, where a healthy Redis call sits.</summary>
    internal static readonly IReadOnlyList<double> StoreDurationBuckets =
        [0.0005, 0.001, 0.0025, 0.005, 0.01, 0.025, 0.05, 0.075, 0.1, 0.25, 0.5, 0.75, 1, 2.5, 5, 7.5, 10];

    private readonly Counter<long> _fallback;
    private readonly Histogram<double> _storeDuration;

    public RedisRateLimitingMetrics(IMeterFactory meterFactory, RedisCircuit circuit)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);
        ArgumentNullException.ThrowIfNull(circuit);

        var meter = meterFactory.Create(new MeterOptions(ApiPipelineTelemetry.MeterName)
        {
            Version = InstrumentationVersion,
        });

        _fallback = meter.CreateCounter<long>(
            "apipipeline.rate_limiting.store.fallback",
            unit: "{request}",
            description:
                "Number of requests decided without Redis because it was unavailable: by the per-pod " +
                "rate limiter, or admitted or rejected outright, as the policy's fallback behavior says.");

        _storeDuration = meter.CreateHistogram(
            "apipipeline.rate_limiting.store.duration",
            unit: "s",
            description:
                "Duration of the Redis call that decides a rate-limited request, until Redis answered, " +
                "the call failed, or the command timeout passed.",
            tags: null,
            advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = StoreDurationBuckets });

        meter.CreateObservableGauge(
            "apipipeline.rate_limiting.store.degraded",
            () => circuit.IsOpen ? 1 : 0,
            // Not "1": Prometheus would append "_ratio" and rename the documented series.
            unit: "{state}",
            description: "1 while the Redis circuit is open, else 0.");
    }

    /// <summary>A request decided without Redis.</summary>
    /// <param name="policyName">The policy.</param>
    /// <param name="reason">A <see cref="FallbackReason"/> value.</param>
    public void Fallback(string policyName, string reason) =>
        _fallback.Add(
            1,
            new KeyValuePair<string, object?>("apipipeline.rate_limiting.policy", policyName),
            new KeyValuePair<string, object?>("apipipeline.rate_limiting.store.fallback_reason", reason));

    /// <summary>A Redis call ended.</summary>
    /// <param name="policyName">The policy.</param>
    /// <param name="duration">Until answer, failure or timeout.</param>
    /// <param name="errorType">Null when Redis answered.</param>
    public void StoreCall(string policyName, TimeSpan duration, string? errorType)
    {
        if (errorType is null)
        {
            _storeDuration.Record(
                duration.TotalSeconds,
                new KeyValuePair<string, object?>("apipipeline.rate_limiting.policy", policyName));
        }
        else
        {
            _storeDuration.Record(
                duration.TotalSeconds,
                new KeyValuePair<string, object?>("apipipeline.rate_limiting.policy", policyName),
                new KeyValuePair<string, object?>("error.type", errorType));
        }
    }
}

/// <summary>The fixed <c>error.type</c> value; otherwise the exception type.</summary>
internal static class StoreErrorType
{
    /// <summary>Redis did not answer in time.</summary>
    public const string Timeout = "timeout";
}

/// <summary>Values of the <c>fallback_reason</c> tag.</summary>
internal static class FallbackReason
{
    /// <summary>The circuit was open.</summary>
    public const string CircuitOpen = "circuit_open";

    /// <summary>Redis did not answer in time.</summary>
    public const string Timeout = "timeout";

    /// <summary>Any other failure.</summary>
    public const string Error = "error";

    /// <summary>The first connection was still being made.</summary>
    public const string Connecting = "connecting";
}
