using Microsoft.Extensions.Logging;

namespace ApiPipeline.NET.RateLimiting.Redis;

/// <summary>
/// Every log line the Redis add-on writes, source-generated with <see cref="LoggerMessageAttribute"/>.
/// Event ids 100-199 (the core package uses 1-99, Versioning 200-299).
/// </summary>
internal static partial class Log
{
    [LoggerMessage(100, LogLevel.Warning, RedisCircuit.UnavailableMessage + " {Fallbacks}")]
    public static partial void SharedRateLimitingUnavailable(this ILogger logger, Exception exception, string fallbacks);

    [LoggerMessage(101, LogLevel.Information, RedisCircuit.RestoredMessage)]
    public static partial void SharedRateLimitingRestored(this ILogger logger);

    [LoggerMessage(102, LogLevel.Debug,
        "A Redis rate limit call failed; this request was decided by its policy's fallback. " +
        "Consecutive failures: {ConsecutiveFailures} of {FailureThreshold} before the circuit opens " +
        "and every policy but the fail-closed ones uses its fallback without asking Redis.")]
    public static partial void SharedRateLimitingCallFailed(
        this ILogger logger, Exception exception, int consecutiveFailures, int failureThreshold);

    [LoggerMessage(103, LogLevel.Warning,
        "A Redis-backed rate limiter (policy '{PolicyName}' among others) was asked to decide " +
        "synchronously (RateLimiter.AttemptAcquire) {Count} times more than asynchronously. It cannot " +
        "reach Redis without blocking, so every synchronous attempt is rejected: code that only calls " +
        "AttemptAcquire (a SignalR hub filter, a custom middleware, a direct use of " +
        "IApiPipelineRateLimiterFactory or the rate limiter options) rejects all of its requests. Call " +
        "AcquireAsync instead, as ASP.NET Core's rate limiting middleware does. Logged once per process.")]
    public static partial void SynchronousAttemptsUnanswered(this ILogger logger, string policyName, int count);

    [LoggerMessage(104, LogLevel.Warning,
        "Redis at {Endpoint} is version {Version}; shared rate limiting needs {MinimumVersion} or later. " +
        "Its scripts may fail there, and every request is then decided by its policy's fallback.")]
    public static partial void RedisVersionUnsupported(this ILogger logger, string endpoint, string version, string minimumVersion);

    [LoggerMessage(105, LogLevel.Warning,
        "Redis at {Endpoint} has a memory limit and maxmemory-policy '{Policy}', so under memory pressure it " +
        "can evict rate limit keys (each has a TTL, so volatile-* policies evict them too), resetting those " +
        "clients' limits without any error. Use 'noeviction' on a Redis dedicated to rate limiting (writes " +
        "then fail, and each policy's fallback decides), or size it so it never reaches maxmemory.")]
    public static partial void RedisEvictsRateLimitKeys(this ILogger logger, string endpoint, string policy);

    [LoggerMessage(106, LogLevel.Debug,
        "Could not check the Redis server at {Endpoint} (version, eviction policy); it may refuse INFO.")]
    public static partial void RedisServerCheckSkipped(this ILogger logger, Exception exception, string endpoint);

    [LoggerMessage(107, LogLevel.Warning,
        "Redis at {Endpoints} is reached without TLS, so rate limit keys, scripts and any password cross " +
        "the network in plain text. Add ssl=true to ApiPipeline:RateLimiting:Redis:Configuration, or set " +
        "Ssl on the connection passed in code. Loopback endpoints (a pod-local TLS proxy) are not reported.")]
    public static partial void RedisWithoutTls(this ILogger logger, string endpoints);
}
