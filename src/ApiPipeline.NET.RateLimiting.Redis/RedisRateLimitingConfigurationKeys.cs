namespace ApiPipeline.NET.RateLimiting.Redis;

/// <summary>
/// Configuration keys consumed by <c>ApiPipeline.NET.RateLimiting.Redis</c>.
/// </summary>
public static class RedisRateLimitingConfigurationKeys
{
    /// <summary>
    /// The Redis settings, relative to the core package's rate limiting section:
    /// <c>ApiPipeline:RateLimiting:Redis</c>.
    /// </summary>
    public const string Redis = "Redis";
}
