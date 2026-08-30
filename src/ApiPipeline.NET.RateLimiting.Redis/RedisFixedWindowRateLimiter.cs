using StackExchange.Redis;

namespace ApiPipeline.NET.RateLimiting.Redis;

/// <summary>
/// <c>PermitLimit</c> per <c>Window</c>, shared by every pod. The window starts at the
/// partition's first request (one counter whose expiry is the window), and a rejection reports the
/// window's remaining time as <c>Retry-After</c>.
/// </summary>
internal sealed class RedisFixedWindowRateLimiter : RedisRateLimiter
{
    private readonly RedisValue[] _arguments;

    public RedisFixedWindowRateLimiter(string partitionKey, ApiPipelineRateLimitPolicy policy, RedisRateLimiterContext context)
        : base(partitionKey, policy, context)
    {
        _arguments = [policy.PermitLimit, (long)(policy.Window ?? TimeSpan.FromMinutes(1)).TotalMilliseconds];
    }

    protected override Task<RedisResult> ExecuteAsync(IDatabase db, RedisKey key, RedisValue leaseId, int permitCount, CancellationToken cancellationToken) =>
        db.ScriptEvaluateAsync(RedisScripts.FixedWindow, [key], [.. _arguments, permitCount]);
}
