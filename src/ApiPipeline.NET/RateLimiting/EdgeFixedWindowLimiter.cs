using System.Threading.RateLimiting;

namespace ApiPipeline.NET.RateLimiting;

/// <summary>
/// One address's edge allowance: <c>PermitLimit</c> per window, starting at the first request. Unlike
/// <see cref="FixedWindowRateLimiter"/>, a rejection's retry-after is what is left of the window.
/// Never queues; rolls over on the next request, so it needs no timer.
/// </summary>
internal sealed class EdgeFixedWindowLimiter : RateLimiter
{
    private static readonly RateLimitLease Acquired = new Lease(isAcquired: true, retryAfter: null);

    private readonly int _permitLimit;
    private readonly TimeSpan _window;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private long _windowStart;
    private int _spent;
    private long _rejected;

    public EdgeFixedWindowLimiter(int permitLimit, TimeSpan window, TimeProvider time)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(permitLimit, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(window, TimeSpan.Zero);
        ArgumentNullException.ThrowIfNull(time);

        _permitLimit = permitLimit;
        _window = window;
        _time = time;
        _windowStart = time.GetTimestamp();
    }

    /// <summary>Time with every permit available; <c>null</c> while permits are spent, so it is not retired mid-window.</summary>
    public override TimeSpan? IdleDuration
    {
        get
        {
            lock (_gate)
            {
                var elapsed = _time.GetElapsedTime(_windowStart);
                if (elapsed >= _window)
                {
                    return elapsed - _window;
                }

                return _spent == 0 ? elapsed : null;
            }
        }
    }

    public override RateLimiterStatistics? GetStatistics()
    {
        lock (_gate)
        {
            RollOver();
            return new RateLimiterStatistics
            {
                CurrentAvailablePermits = _permitLimit - _spent,
                CurrentQueuedCount = 0,
                TotalFailedLeases = Interlocked.Read(ref _rejected),
            };
        }
    }

    protected override RateLimitLease AttemptAcquireCore(int permitCount)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(permitCount, _permitLimit);

        lock (_gate)
        {
            RollOver();

            if (permitCount == 0 ? _spent < _permitLimit : _spent + permitCount <= _permitLimit)
            {
                _spent += permitCount;
                return Acquired;
            }

            Interlocked.Increment(ref _rejected);
            return new Lease(isAcquired: false, retryAfter: _window - _time.GetElapsedTime(_windowStart));
        }
    }

    protected override ValueTask<RateLimitLease> AcquireAsyncCore(int permitCount, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(AttemptAcquireCore(permitCount));
    }

    /// <summary>Starts a new window when the current one ended. Under the lock.</summary>
    private void RollOver()
    {
        if (_time.GetElapsedTime(_windowStart) >= _window)
        {
            _windowStart = _time.GetTimestamp();
            _spent = 0;
        }
    }

    private sealed class Lease(bool isAcquired, TimeSpan? retryAfter) : RateLimitLease
    {
        public override bool IsAcquired { get; } = isAcquired;

        public override IEnumerable<string> MetadataNames =>
            retryAfter is null ? [] : [MetadataName.RetryAfter.Name];

        public override bool TryGetMetadata(string metadataName, out object? metadata)
        {
            if (retryAfter is { } value && metadataName == MetadataName.RetryAfter.Name)
            {
                metadata = value;
                return true;
            }

            metadata = null;
            return false;
        }
    }
}
