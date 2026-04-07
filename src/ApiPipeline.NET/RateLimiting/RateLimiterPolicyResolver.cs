using System.Collections.Frozen;
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
    private volatile PolicySnapshot _snapshot;

    public RateLimiterPolicyResolver(IOptionsMonitor<RateLimitingOptions> monitor)
    {
        _monitor = monitor;
        _snapshot = BuildSnapshot(monitor.CurrentValue);
        _monitor.OnChange(options => _snapshot = BuildSnapshot(options));
    }

    public RateLimitingOptions Current => _snapshot.Options;

    public RateLimitPolicy? ResolvePolicy(string policyName)
    {
        if (string.IsNullOrWhiteSpace(policyName))
        {
            return null;
        }

        return _snapshot.Policies.TryGetValue(policyName, out var policy)
            ? policy
            : null;
    }

    private static PolicySnapshot BuildSnapshot(RateLimitingOptions options)
    {
        var policies = new Dictionary<string, RateLimitPolicy>(StringComparer.OrdinalIgnoreCase);
        foreach (var policy in options.Policies)
        {
            if (!string.IsNullOrWhiteSpace(policy.Name))
            {
                policies[policy.Name] = policy;
            }
        }

        return new PolicySnapshot(options, policies.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase));
    }

    private sealed record PolicySnapshot(
        RateLimitingOptions Options,
        FrozenDictionary<string, RateLimitPolicy> Policies);
}
