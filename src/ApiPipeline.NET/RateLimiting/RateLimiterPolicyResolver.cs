using System.Collections.Frozen;
using Microsoft.Extensions.Options;

namespace ApiPipeline.NET.RateLimiting;

/// <summary>Policies, partition scopes and allowed key hashes, precomputed once from the startup options.</summary>
internal sealed class RateLimiterPolicyResolver
{
    private readonly Snapshot _snapshot;

    public RateLimiterPolicyResolver(IOptions<ApiPipelineRateLimitingOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _snapshot = Build(options.Value);
    }

    public ApiPipelineRateLimitingOptions Current => _snapshot.Options;

    /// <summary>The policy and its scope: name plus limits signature, so a changed limit gets new buckets.</summary>
    public bool TryResolve(string policyName, out ApiPipelineRateLimitPolicy policy, out string scope)
    {
        if (_snapshot.Policies.TryGetValue(policyName, out var entry))
        {
            (policy, scope) = entry;
            return true;
        }

        policy = null!;
        scope = "-";
        return false;
    }

    /// <summary>Whether the hash is allow-listed; anything else falls through to IP.</summary>
    public bool IsKnownApiKeyHash(string keyHash) => _snapshot.KnownApiKeyHashes.Contains(keyHash);

    public string[] PartitionClaims => _snapshot.PartitionClaims;

    private static Snapshot Build(ApiPipelineRateLimitingOptions options) => new(
        options,
        options.Policies
            .Where(p => !string.IsNullOrWhiteSpace(p.Name))
            .ToFrozenDictionary(
                p => p.Name,
                p => (p, RateLimitPolicySignature.Compute(p)),
                StringComparer.OrdinalIgnoreCase),
        (options.KnownApiKeyHashes ?? [])
            .Where(h => !string.IsNullOrWhiteSpace(h))
            .Select(h => h.Trim())
            .ToFrozenSet(StringComparer.OrdinalIgnoreCase),
        [.. (options.PartitionClaims ?? []).Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c.Trim())]);

    private sealed record Snapshot(
        ApiPipelineRateLimitingOptions Options,
        FrozenDictionary<string, (ApiPipelineRateLimitPolicy Policy, string Scope)> Policies,
        FrozenSet<string> KnownApiKeyHashes,
        string[] PartitionClaims);
}
