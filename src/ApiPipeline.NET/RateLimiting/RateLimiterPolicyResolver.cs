using ApiPipeline.NET.Options;
using Microsoft.Extensions.Options;

namespace ApiPipeline.NET.RateLimiting;

/// <summary>
/// Singleton resolver for rate limit policies. Wraps <see cref="IOptionsMonitor{T}"/>
/// to avoid per-request DI scope allocation inside the rate-limiter callback.
/// </summary>
internal sealed class RateLimiterPolicyResolver
{
    private readonly IOptionsMonitor<RateLimitingOptions> _monitor;

    public RateLimiterPolicyResolver(IOptionsMonitor<RateLimitingOptions> monitor)
        => _monitor = monitor;

    public RateLimitingOptions Current => _monitor.CurrentValue;
}
