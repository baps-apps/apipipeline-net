using StackExchange.Redis;

namespace ApiPipeline.NET.RateLimiting.Redis;

/// <summary>
/// A bucket of <c>PermitLimit</c> tokens shared by every pod, refilled by <c>TokensPerPeriod</c>
/// each whole <c>Window</c>. A rejection reports the time to the next refill.
/// </summary>
internal sealed class RedisTokenBucketRateLimiter : RedisRateLimiter
{
    private readonly RedisValue[] _arguments;

    public RedisTokenBucketRateLimiter(string partitionKey, ApiPipelineRateLimitPolicy policy, RedisRateLimiterContext context)
        : base(partitionKey, policy, context)
    {
        _arguments = [policy.PermitLimit, policy.TokensPerPeriod ?? 1, (long)(policy.Window ?? TimeSpan.FromMinutes(1)).TotalMilliseconds];
    }

    protected override Task<RedisResult> ExecuteAsync(IDatabase db, RedisKey key, RedisValue leaseId, int permitCount, CancellationToken cancellationToken) =>
        db.ScriptEvaluateAsync(RedisScripts.TokenBucket, [key], [.. _arguments, permitCount]);
}
