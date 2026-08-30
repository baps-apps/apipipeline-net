using System.Text;
using System.Threading.RateLimiting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace ApiPipeline.NET.RateLimiting.Redis;

/// <summary>
/// Backs each partition with a Redis limiter. With sharing off, delegates to
/// <see cref="InMemoryRateLimiterFactory"/>: installing the package is not opting in.
/// </summary>
internal sealed class RedisRateLimiterFactory : IApiPipelineRateLimiterFactory
{
    private readonly InMemoryRateLimiterFactory _inMemory = new();
    private readonly bool _enabled;
    private readonly RedisRateLimiterContext _context;

    public RedisRateLimiterFactory(
        IOptions<ApiPipelineRedisRateLimitingOptions> options,
        RedisConnectionSource connection,
        IHostEnvironment environment,
        TimeProvider timeProvider,
        RedisCircuit circuit,
        RedisRateLimitingMetrics metrics,
        SynchronousAttemptMonitor synchronousAttempts,
        RedisServerCheck serverCheck)
    {
        var settings = options.Value;
        _enabled = settings.Enabled;
        KeyPrefix = settings.KeyPrefix ?? $"{environment.ApplicationName}:{environment.EnvironmentName}";
        _context = new RedisRateLimiterContext(
            connection,
            KeyPrefix,
            settings.CommandTimeout,
            timeProvider,
            _inMemory,
            circuit,
            metrics,
            settings.ConcurrencyLeaseTimeout,
            settings.Fallback,
            settings.PolicyFallbacks,
            synchronousAttempts,
            serverCheck,
            settings.PartitionKeySecret is { } secret ? Encoding.UTF8.GetBytes(secret) : null,
            settings.ConcurrencyLeaseMaxDuration);
    }

    /// <summary>The prefix of every key: the configured one, else <c>{ApplicationName}:{EnvironmentName}</c>.</summary>
    internal string KeyPrefix { get; }

    public RateLimiter Create(string partitionKey, ApiPipelineRateLimitPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(partitionKey);
        ArgumentNullException.ThrowIfNull(policy);

        if (!_enabled)
        {
            return _inMemory.Create(partitionKey, policy);
        }

        return policy.Kind switch
        {
            RateLimiterKind.FixedWindow => new RedisFixedWindowRateLimiter(partitionKey, policy, _context),
            RateLimiterKind.SlidingWindow => new RedisSlidingWindowRateLimiter(partitionKey, policy, _context),
            RateLimiterKind.TokenBucket => new RedisTokenBucketRateLimiter(partitionKey, policy, _context),
            RateLimiterKind.Concurrency => new RedisConcurrencyLimiter(partitionKey, policy, _context),

            _ => _inMemory.Create(partitionKey, policy),
        };
    }
}
