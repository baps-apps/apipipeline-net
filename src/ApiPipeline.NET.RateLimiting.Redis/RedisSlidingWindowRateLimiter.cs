using StackExchange.Redis;

namespace ApiPipeline.NET.RateLimiting.Redis;

/// <summary>
/// <c>PermitLimit</c> per sliding <c>Window</c>, split into <c>SegmentsPerWindow</c>
/// segments, shared by every pod. Permits return as segments leave the window, and a rejection
/// reports when the oldest counted segment does.
/// </summary>
internal sealed class RedisSlidingWindowRateLimiter : RedisRateLimiter
{
    private readonly RedisValue[] _arguments;

    public RedisSlidingWindowRateLimiter(string partitionKey, ApiPipelineRateLimitPolicy policy, RedisRateLimiterContext context)
        : base(partitionKey, policy, context)
    {
        _arguments = [policy.PermitLimit, (long)(policy.Window ?? TimeSpan.FromMinutes(1)).TotalMilliseconds, policy.SegmentsPerWindow ?? 4];
    }

    protected override Task<RedisResult> ExecuteAsync(IDatabase db, RedisKey key, RedisValue leaseId, int permitCount, CancellationToken cancellationToken) =>
        db.ScriptEvaluateAsync(RedisScripts.SlidingWindow, [key], [.. _arguments, permitCount]);
}
