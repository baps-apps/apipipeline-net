using System.Threading.RateLimiting;

namespace ApiPipeline.NET.RateLimiting;

/// <summary>
/// Creates the limiter behind one rate-limit partition. The default is
/// <see cref="InMemoryRateLimiterFactory"/>; the Redis package supplies its own.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Called once per partition; must be thread-safe.</item>
/// <item>A limiter wrapping a <see cref="ReplenishingRateLimiter"/> must itself be one and forward
/// <see cref="ReplenishingRateLimiter.TryReplenish"/>, or the wrapped limiter never refills.</item>
/// <item>A store-backed limiter may answer every <see cref="RateLimiter.AttemptAcquire(int)"/>
/// "no", but never "yes" without its store: the middleware treats that answer as final.</item>
/// </list>
/// </remarks>
public interface IApiPipelineRateLimiterFactory
{
    /// <summary>Creates the limiter for one partition.</summary>
    /// <param name="partitionKey">The partition key, identical across replicas; includes the policy signature.</param>
    /// <param name="policy">The policy that governs the partition.</param>
    /// <returns>A limiter enforcing <paramref name="policy"/> for the partition.</returns>
    RateLimiter Create(string partitionKey, ApiPipelineRateLimitPolicy policy);
}
