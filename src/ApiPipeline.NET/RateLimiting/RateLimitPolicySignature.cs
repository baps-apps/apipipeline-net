using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ApiPipeline.NET.RateLimiting;

/// <summary>
/// A signature of a policy's limits, added to partition keys, so during a rollout old and new
/// replicas sharing Redis count in separate buckets.
/// </summary>
internal static class RateLimitPolicySignature
{
    /// <summary>The policy name plus a hash of every limit-defining field.</summary>
    public static string Compute(ApiPipelineRateLimitPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        // Must match across processes, so not HashCode.Combine (randomized per process).
        var canonical = string.Create(
            CultureInfo.InvariantCulture,
            $"{(int)policy.Kind}|{policy.PermitLimit}|{policy.QueueLimit}|{Format(policy.WindowInSeconds)}|{Format(policy.SegmentsPerWindow)}|{Format(policy.TokensPerPeriod)}|{(int)policy.QueueProcessingOrder}");

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        var hash = Convert.ToHexStringLower(digest.AsSpan(0, 8));

        var name = string.IsNullOrWhiteSpace(policy.Name) ? "-" : policy.Name;
        return string.Create(CultureInfo.InvariantCulture, $"{name}:{hash}");
    }

    private static string Format(int? value)
        => value?.ToString(CultureInfo.InvariantCulture) ?? "-";
}
