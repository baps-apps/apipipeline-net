using System.Threading.RateLimiting;
using ApiPipeline.NET.RateLimiting;
using FluentAssertions;

namespace ApiPipeline.NET.Tests;

/// <summary>
/// The policy signature is embedded in rate limit partition keys that are shared across processes (Redis), so it
/// must be identical in every process and must change whenever a limit-defining field changes.
/// </summary>
public sealed class RateLimitPolicySignatureTests
{
    private const string ExpectedGolden = "strict:b4ef179343787d89";

    private static ApiPipelineRateLimitPolicy Baseline() => new()
    {
        Name = "strict",
        Kind = RateLimiterKind.FixedWindow,
        PermitLimit = 100,
        Window = TimeSpan.FromSeconds(60),
    };

    /// <summary>
    /// A pinned golden value proves the hash is not seeded per process: <c>HashCode.Combine</c> could never pass this,
    /// and replicas computing different signatures would split one policy into disjoint Redis partitions.
    /// </summary>
    [Fact]
    public void Compute_IsTheSameInEveryProcess()
    {
        RateLimitPolicySignature.Compute(Baseline()).Should().Be(ExpectedGolden);
    }

    /// <summary>
    /// A changed limit produces a new partition key, so no stored limiter keeps the previous limit.
    /// </summary>
    [Theory]
    [InlineData(nameof(ApiPipelineRateLimitPolicy.PermitLimit))]
    [InlineData(nameof(ApiPipelineRateLimitPolicy.Window))]
    [InlineData(nameof(ApiPipelineRateLimitPolicy.Kind))]
    [InlineData(nameof(ApiPipelineRateLimitPolicy.QueueLimit))]
    [InlineData(nameof(ApiPipelineRateLimitPolicy.SegmentsPerWindow))]
    [InlineData(nameof(ApiPipelineRateLimitPolicy.TokensPerPeriod))]
    [InlineData(nameof(ApiPipelineRateLimitPolicy.QueueProcessingOrder))]
    public void Compute_ChangesWhenALimitChanges(string field)
    {
        var baseline = Baseline();
        var mutated = Baseline();

        switch (field)
        {
            case nameof(ApiPipelineRateLimitPolicy.PermitLimit): mutated.PermitLimit = 101; break;
            case nameof(ApiPipelineRateLimitPolicy.Window): mutated.Window = TimeSpan.FromSeconds(61); break;
            case nameof(ApiPipelineRateLimitPolicy.Kind): mutated.Kind = RateLimiterKind.SlidingWindow; break;
            case nameof(ApiPipelineRateLimitPolicy.QueueLimit): mutated.QueueLimit = 5; break;
            case nameof(ApiPipelineRateLimitPolicy.SegmentsPerWindow): mutated.SegmentsPerWindow = 4; break;
            case nameof(ApiPipelineRateLimitPolicy.TokensPerPeriod): mutated.TokensPerPeriod = 7; break;
            case nameof(ApiPipelineRateLimitPolicy.QueueProcessingOrder): mutated.QueueProcessingOrder = QueueProcessingOrder.NewestFirst; break;
            default: throw new ArgumentOutOfRangeException(nameof(field), field, null);
        }

        RateLimitPolicySignature.Compute(mutated).Should().NotBe(RateLimitPolicySignature.Compute(baseline));
    }
}
