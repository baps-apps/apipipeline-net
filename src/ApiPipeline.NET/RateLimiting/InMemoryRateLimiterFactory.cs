using System.Threading.RateLimiting;

namespace ApiPipeline.NET.RateLimiting;

/// <summary>
/// The default factory: ASP.NET Core's in-process limiters, so each replica limits on its own.
/// </summary>
/// <remarks>
/// Built like the framework's <c>RateLimitPartition.Get*Limiter</c> helpers, without timers: the
/// partitioned limiter replenishes them. A limiter used on its own must be replenished by its owner.
/// </remarks>
public sealed class InMemoryRateLimiterFactory : IApiPipelineRateLimiterFactory
{
    /// <inheritdoc />
    /// <exception cref="NotSupportedException">The policy's kind is not supported.</exception>
    public RateLimiter Create(string partitionKey, ApiPipelineRateLimitPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        return policy.Kind switch
        {
            RateLimiterKind.FixedWindow => new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions
            {
                PermitLimit = policy.PermitLimit,
                Window = policy.Window ?? TimeSpan.FromMinutes(1),
                QueueLimit = policy.QueueLimit,
                QueueProcessingOrder = policy.QueueProcessingOrder,
                AutoReplenishment = false,
            }),

            RateLimiterKind.SlidingWindow => new SlidingWindowRateLimiter(new SlidingWindowRateLimiterOptions
            {
                PermitLimit = policy.PermitLimit,
                Window = policy.Window ?? TimeSpan.FromMinutes(1),
                SegmentsPerWindow = policy.SegmentsPerWindow ?? 4,
                QueueLimit = policy.QueueLimit,
                QueueProcessingOrder = policy.QueueProcessingOrder,
                AutoReplenishment = false,
            }),

            RateLimiterKind.Concurrency => new ConcurrencyLimiter(new ConcurrencyLimiterOptions
            {
                PermitLimit = policy.PermitLimit,
                QueueLimit = policy.QueueLimit,
                QueueProcessingOrder = policy.QueueProcessingOrder,
            }),

            RateLimiterKind.TokenBucket => new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
            {
                TokenLimit = policy.PermitLimit,
                ReplenishmentPeriod = policy.Window ?? TimeSpan.FromMinutes(1),
                TokensPerPeriod = policy.TokensPerPeriod ?? 1,
                QueueLimit = policy.QueueLimit,
                QueueProcessingOrder = policy.QueueProcessingOrder,
                AutoReplenishment = false,
            }),

            _ => throw new NotSupportedException($"Unsupported rate limiter kind '{policy.Kind}'."),
        };
    }
}
