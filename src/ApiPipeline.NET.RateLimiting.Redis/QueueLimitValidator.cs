using Microsoft.Extensions.Options;

namespace ApiPipeline.NET.RateLimiting.Redis;

/// <summary>
/// With sharing on, fails startup for what Redis cannot run: <c>QueueLimit &gt; 0</c>, sliding windows
/// with more than <see cref="MaxSegmentsPerWindow"/> segments or not evenly divisible into whole-ms
/// segments, and a <c>PolicyFallbacks</c> entry naming no policy.
/// </summary>
internal sealed class QueueLimitValidator : IValidateOptions<ApiPipelineRateLimitingOptions>
{
    internal const int MaxSegmentsPerWindow = 100;

    private readonly IOptions<ApiPipelineRedisRateLimitingOptions> _redis;

    public QueueLimitValidator(IOptions<ApiPipelineRedisRateLimitingOptions> redis)
    {
        _redis = redis;
    }

    public ValidateOptionsResult Validate(string? name, ApiPipelineRateLimitingOptions options)
    {
        if (!_redis.Value.Enabled)
        {
            return ValidateOptionsResult.Skip;
        }

        List<string>? failures = null;
        for (var i = 0; i < options.Policies.Count; i++)
        {
            var policy = options.Policies[i];
            if (policy.QueueLimit > 0)
            {
                (failures ??= []).Add(
                    $"Policies[{i}] '{policy.Name}': QueueLimit must be 0 when ApiPipeline:RateLimiting:Redis:Enabled " +
                    "is true. Shared (Redis) limiters admit or reject; they cannot hold a request in a queue across pods.");
            }

            if (policy.Kind == RateLimiterKind.SlidingWindow)
            {
                var segments = policy.SegmentsPerWindow ?? 4;
                var windowMs = (long)(policy.Window ?? TimeSpan.FromMinutes(1)).TotalMilliseconds;

                if (segments > MaxSegmentsPerWindow)
                {
                    (failures ??= []).Add(
                        $"Policies[{i}] '{policy.Name}': SegmentsPerWindow must be at most {MaxSegmentsPerWindow} when " +
                        "ApiPipeline:RateLimiting:Redis:Enabled is true; the shared (Redis) sliding window reads every " +
                        "segment on every request.");
                }

                // Whole-ms segments: an uneven split shortens the window (1000 ms / 600 = 600 ms).
                else if (windowMs % segments != 0)
                {
                    (failures ??= []).Add(
                        $"Policies[{i}] '{policy.Name}': Window ({windowMs} ms) must " +
                        $"divide evenly into SegmentsPerWindow ({segments}) when ApiPipeline:RateLimiting:Redis:Enabled is " +
                        $"true; the shared (Redis) sliding window counts whole-millisecond segments, so the window would " +
                        $"shrink to {windowMs / segments * segments} ms. Choose a segment count that divides {windowMs}.");
                }
            }
        }

        // A misspelled name would fail open. Here, not in the Redis options' validator: circular.
        var names = options.Policies
            .Select(p => p.Name)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var policyName in _redis.Value.PolicyFallbacks?.Keys ?? [])
        {
            if (!names.Contains(policyName))
            {
                (failures ??= []).Add(
                    $"ApiPipeline:RateLimiting:Redis:PolicyFallbacks names '{policyName}', which is not a configured " +
                    $"policy (configured: {string.Join(", ", names.Order(StringComparer.OrdinalIgnoreCase))}). A " +
                    "misspelled name leaves the real policy on the default Fallback.");
            }
        }

        return failures is null ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
