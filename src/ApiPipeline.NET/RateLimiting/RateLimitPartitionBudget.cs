using System.Collections.Concurrent;
using System.Threading.RateLimiting;
using ApiPipeline.NET.Observability;

namespace ApiPipeline.NET.RateLimiting;

/// <summary>
/// Caps the partitions a limiter keeps, since callers can mint addresses (and identities, with many
/// accounts) and each partition costs memory and heartbeat time. Past the cap, <see cref="Admit"/>
/// returns the overflow key (one shared bucket, failing closed). Approximate under concurrency.
/// </summary>
internal sealed class RateLimitPartitionBudget
{
    /// <summary>Limiter names for logs and metrics.</summary>
    public const string MainLimiter = "ApiPipeline:RateLimiting";

    public const string EdgeLimiter = "ApiPipeline:EdgeRateLimiting";

    public const string MainLimiterIdentities = "ApiPipeline:RateLimiting (identities)";

    private static readonly long WarningIntervalMilliseconds = (long)TimeSpan.FromMinutes(1).TotalMilliseconds;

    // Key -> live limiters holding it (global and endpoint limiters may share a key).
    private readonly ConcurrentDictionary<string, int> _live = new(StringComparer.Ordinal);
    private readonly int _maxPartitions;
    private readonly string _limiter;
    private readonly ApiPipelineMetrics? _metrics;
    private readonly ILogger _logger;
    private int _count;
    private long _nextWarningAt = long.MinValue;

    /// <param name="maxPartitions">The cap.</param>
    /// <param name="limiter">The limiter's name.</param>
    /// <param name="metrics">Counts overflows; <c>null</c> in unit tests.</param>
    /// <param name="logger">Gets the overflow warning, at most once a minute.</param>
    public RateLimitPartitionBudget(int maxPartitions, string limiter, ApiPipelineMetrics? metrics, ILogger logger)
    {
        _maxPartitions = maxPartitions;
        _limiter = limiter;
        _metrics = metrics;
        _logger = logger;
    }

    public int Count => Volatile.Read(ref _count);

    /// <summary><paramref name="key"/> if it has a partition or there is room, else <paramref name="overflowKey"/>.</summary>
    public string Admit(string key, string overflowKey) => TryAdmit(key) ? key : overflowKey;

    /// <summary>Whether <paramref name="key"/> may have a partition. A refusal is counted and logged.</summary>
    public bool TryAdmit(string key)
    {
        if (Volatile.Read(ref _count) < _maxPartitions || _live.ContainsKey(key))
        {
            return true;
        }

        Overflowed();
        return false;
    }

    /// <summary>Counts a new partition; disposing the returned limiter frees the slot.</summary>
    public RateLimiter Track(string key, RateLimiter limiter)
    {
        Acquire(key);
        return limiter is ReplenishingRateLimiter replenishing
            ? new TrackedReplenishingRateLimiter(replenishing, this, key)
            : new TrackedRateLimiter(limiter, this, key);
    }

    private void Acquire(string key)
    {
        while (true)
        {
            if (_live.TryGetValue(key, out var holders))
            {
                if (_live.TryUpdate(key, holders + 1, holders))
                {
                    return;
                }
            }
            else if (_live.TryAdd(key, 1))
            {
                Interlocked.Increment(ref _count);
                return;
            }
        }
    }

    private void Release(string key)
    {
        while (true)
        {
            if (!_live.TryGetValue(key, out var holders))
            {
                return;
            }

            if (holders == 1)
            {
                if (_live.TryRemove(new KeyValuePair<string, int>(key, 1)))
                {
                    Interlocked.Decrement(ref _count);
                    return;
                }
            }
            else if (_live.TryUpdate(key, holders - 1, holders))
            {
                return;
            }
        }
    }

    private void Overflowed()
    {
        switch (_limiter)
        {
            case EdgeLimiter:
                _metrics?.EdgeRateLimitPartitionOverflow();
                break;
            case MainLimiterIdentities:
                _metrics?.RateLimitPartitionOverflow("identity");
                break;
            default:
                _metrics?.RateLimitPartitionOverflow("ip");
                break;
        }

        var now = Environment.TickCount64;
        var next = Volatile.Read(ref _nextWarningAt);
        if (now >= next && Interlocked.CompareExchange(ref _nextWarningAt, now + WarningIntervalMilliseconds, next) == next)
        {
            if (_limiter == MainLimiterIdentities)
            {
                _logger.IdentityPartitionsExhausted(_maxPartitions);
            }
            else
            {
                _logger.RateLimitPartitionsExhausted(_limiter, _maxPartitions);
            }
        }
    }

    private sealed class Slot(RateLimitPartitionBudget budget, string key)
    {
        private int _released;

        public void Release()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                budget.Release(key);
            }
        }
    }

    private sealed class TrackedRateLimiter(RateLimiter inner, RateLimitPartitionBudget budget, string key) : RateLimiter
    {
        private readonly Slot _slot = new(budget, key);

        public override TimeSpan? IdleDuration => inner.IdleDuration;

        public override RateLimiterStatistics? GetStatistics() => inner.GetStatistics();

        protected override RateLimitLease AttemptAcquireCore(int permitCount) => inner.AttemptAcquire(permitCount);

        protected override ValueTask<RateLimitLease> AcquireAsyncCore(int permitCount, CancellationToken cancellationToken) =>
            inner.AcquireAsync(permitCount, cancellationToken);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                _slot.Release();
            }

            base.Dispose(disposing);
        }

        protected override async ValueTask DisposeAsyncCore()
        {
            await inner.DisposeAsync().ConfigureAwait(false);
            _slot.Release();
            await base.DisposeAsyncCore().ConfigureAwait(false);
        }
    }

    private sealed class TrackedReplenishingRateLimiter(
        ReplenishingRateLimiter inner,
        RateLimitPartitionBudget budget,
        string key) : ReplenishingRateLimiter
    {
        private readonly Slot _slot = new(budget, key);

        public override TimeSpan? IdleDuration => inner.IdleDuration;

        public override bool IsAutoReplenishing => inner.IsAutoReplenishing;

        public override TimeSpan ReplenishmentPeriod => inner.ReplenishmentPeriod;

        public override bool TryReplenish() => inner.TryReplenish();

        public override RateLimiterStatistics? GetStatistics() => inner.GetStatistics();

        protected override RateLimitLease AttemptAcquireCore(int permitCount) => inner.AttemptAcquire(permitCount);

        protected override ValueTask<RateLimitLease> AcquireAsyncCore(int permitCount, CancellationToken cancellationToken) =>
            inner.AcquireAsync(permitCount, cancellationToken);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                _slot.Release();
            }

            base.Dispose(disposing);
        }

        protected override async ValueTask DisposeAsyncCore()
        {
            await inner.DisposeAsync().ConfigureAwait(false);
            _slot.Release();
            await base.DisposeAsyncCore().ConfigureAwait(false);
        }
    }
}
