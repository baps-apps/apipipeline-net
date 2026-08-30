using System.Threading.RateLimiting;

namespace ApiPipeline.NET.RateLimiting;

/// <summary>Never grants a permit, for <see cref="AnonymousFallbackBehavior.Reject"/>. <c>PermitLimit = 0</c> would throw (a 500).</summary>
internal sealed class DenyAllRateLimiter : RateLimiter
{
    public static readonly DenyAllRateLimiter Instance = new();

    private static readonly RateLimiterStatistics EmptyStatistics = new()
    {
        CurrentAvailablePermits = 0,
        CurrentQueuedCount = 0,
        TotalFailedLeases = 0,
        TotalSuccessfulLeases = 0
    };

    private DenyAllRateLimiter()
    {
    }

    /// <summary>Never idle, so the partitioned limiter does not dispose the shared instance.</summary>
    public override TimeSpan? IdleDuration => null;

    /// <inheritdoc />
    public override RateLimiterStatistics GetStatistics() => EmptyStatistics;

    /// <inheritdoc />
    protected override RateLimitLease AttemptAcquireCore(int permitCount) => DeniedLease.Instance;

    /// <inheritdoc />
    protected override ValueTask<RateLimitLease> AcquireAsyncCore(
        int permitCount,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new ValueTask<RateLimitLease>(DeniedLease.Instance);
    }

    private sealed class DeniedLease : RateLimitLease
    {
        public static readonly DeniedLease Instance = new();

        public override bool IsAcquired => false;

        public override IEnumerable<string> MetadataNames => [];

        public override bool TryGetMetadata(string metadataName, out object? metadata)
        {
            metadata = null;
            return false;
        }
    }
}
