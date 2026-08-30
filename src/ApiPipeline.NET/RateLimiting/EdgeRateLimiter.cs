using System.Globalization;
using System.Threading.RateLimiting;
using ApiPipeline.NET.Observability;
using Microsoft.Extensions.Options;

namespace ApiPipeline.NET.RateLimiting;

/// <summary>
/// The edge limiter: one fixed window per client address. A container-owned singleton, so its
/// timer stops with the host; middleware is never disposed.
/// </summary>
internal sealed class EdgeRateLimiter : IDisposable, IAsyncDisposable
{
    private readonly PartitionedRateLimiter<HttpContext> _limiter;

    /// <param name="options">Edge settings.</param>
    /// <param name="partitioning">The main limiter's options, for the IPv6 prefix.</param>
    /// <param name="metrics">Counts overflowed requests.</param>
    /// <param name="loggerFactory">For the overflow warning.</param>
    /// <param name="time">The clock.</param>
    public EdgeRateLimiter(
        IOptions<ApiPipelineEdgeRateLimitingOptions> options,
        IOptions<ApiPipelineRateLimitingOptions> partitioning,
        ApiPipelineMetrics metrics,
        ILoggerFactory loggerFactory,
        TimeProvider time)
    {
        var settings = options.Value;
        var prefix = partitioning.Value.IPv6PartitionPrefixLength;
        var budget = new RateLimitPartitionBudget(
            settings.MaxIpPartitions,
            RateLimitPartitionBudget.EdgeLimiter,
            metrics,
            loggerFactory.CreateLogger("ApiPipeline.NET.EdgeRateLimiting"));
        _limiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        {
            var address = context.Connection.RemoteIpAddress;

            if (address is null)
            {
                // No address: the main limiter's AnonymousFallback decides.
                return RateLimitPartition.GetNoLimiter("edge:unknown");
            }

            var key = budget.Admit(
                string.Create(CultureInfo.InvariantCulture, $"edge:{RateLimitPartitionKeys.NormalizeIp(address, prefix)}"),
                "edge:overflow");

            return RateLimitPartition.Get(key, k => budget.Track(
                k, new EdgeFixedWindowLimiter(settings.PermitLimit, settings.Window, time)));
        });
    }

    /// <summary>Takes one permit for the client address, without waiting.</summary>
    public RateLimitLease AttemptAcquire(HttpContext context) => _limiter.AttemptAcquire(context);

    /// <inheritdoc />
    public void Dispose() => _limiter.Dispose();

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _limiter.DisposeAsync();
}
