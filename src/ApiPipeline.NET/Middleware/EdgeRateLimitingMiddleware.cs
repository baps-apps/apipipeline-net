using System.Threading.RateLimiting;
using ApiPipeline.NET.Observability;
using ApiPipeline.NET.RateLimiting;
using Microsoft.Extensions.Options;

namespace ApiPipeline.NET.Middleware;

/// <summary>
/// Sheds floods before authentication, partitioned by client address. A plain partitioned limiter,
/// not a second <c>UseRateLimiter</c>, which would also apply endpoint policies.
/// </summary>
internal sealed class EdgeRateLimitingMiddleware
{
    /// <summary>The policy name in an edge rejection's rate-limit fields.</summary>
    internal const string PolicyName = "edge";

    private readonly RequestDelegate _next;
    private readonly ApiPipelineEdgeRateLimitingOptions _options;
    private readonly ApiPipelineRateLimitingOptions _partitioning;
    private readonly PathString[] _excludedPaths;
    private readonly System.Net.IPNetwork[] _probeNetworks;
    private readonly ApiPipelineMetrics _metrics;
    private readonly EdgeRateLimiter _limiter;

    public EdgeRateLimitingMiddleware(
        RequestDelegate next,
        IOptions<ApiPipelineEdgeRateLimitingOptions> options,
        IOptions<ApiPipelineRateLimitingOptions> partitioning,
        EdgeRateLimiter limiter,
        ApiPipelineMetrics metrics)
    {
        _next = next;
        _options = options.Value;
        _partitioning = partitioning.Value;
        _limiter = limiter;
        _metrics = metrics;
        _excludedPaths = ProbePaths.Resolve(_options.ExcludedPaths, ApiPipelineEdgeRateLimitingOptions.DefaultExcludedPaths);
        _probeNetworks = ProbePaths.ResolveNetworks(_partitioning.ProbeNetworks);
    }

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var settings = _options;
        if (ProbeExemption.Applies(context, _excludedPaths, _probeNetworks))
        {
            await _next(context);
            return;
        }

        using var lease = _limiter.AttemptAcquire(context);
        if (lease.IsAcquired)
        {
            await _next(context);
            return;
        }

        // A counter, not a log line, which a flood would multiply.
        _metrics.EdgeRateLimitRejected();

        var windowSeconds = (int)Math.Ceiling(settings.Window.TotalSeconds);
        var retryAfterSeconds = lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter)
            ? Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds))
            : windowSeconds;

        await RateLimitRejection.WriteAsync(
            context,
            PolicyName,
            retryAfterSeconds: retryAfterSeconds,
            permitLimit: settings.PermitLimit,
            windowSeconds: windowSeconds,
            emitQuotaHeaders: _partitioning.EmitRateLimitHeadersOnRejection);
    }

}
