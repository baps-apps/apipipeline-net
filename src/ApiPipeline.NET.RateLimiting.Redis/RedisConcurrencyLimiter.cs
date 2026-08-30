using System.Threading.RateLimiting;
using StackExchange.Redis;

namespace ApiPipeline.NET.RateLimiting.Redis;

/// <summary>
/// At most <c>PermitLimit</c> permits in flight across pods, as members of a sorted set. A held lease
/// renews every third of the lease timeout, so a dead pod's slot frees within one timeout. A lease
/// dropped undisposed is released once collected, or after the maximum duration.
/// </summary>
internal sealed class RedisConcurrencyLimiter : RedisRateLimiter
{
    private readonly RedisValue _permitLimit;
    private readonly RedisValue _leaseTimeoutMs;
    private long _outstandingLeases;

    public RedisConcurrencyLimiter(string partitionKey, ApiPipelineRateLimitPolicy policy, RedisRateLimiterContext context)
        : base(partitionKey, policy, context)
    {
        _permitLimit = policy.PermitLimit;
        _leaseTimeoutMs = (long)context.ConcurrencyLeaseTimeout.TotalMilliseconds;
    }

    protected override bool HasOutstandingLeases => Interlocked.Read(ref _outstandingLeases) > 0;

    protected override RedisValue NewLeaseId() => Guid.NewGuid().ToString("N");

    protected override Task<RedisResult> ExecuteAsync(IDatabase db, RedisKey key, RedisValue leaseId, int permitCount, CancellationToken cancellationToken) =>
        db.ScriptEvaluateAsync(RedisScripts.ConcurrencyAcquire, [key], [_permitLimit, _leaseTimeoutMs, leaseId, permitCount]);

    protected override RateLimitLease CreateAcquiredLease(RedisValue leaseId, int permitCount)
    {
        Interlocked.Increment(ref _outstandingLeases);
        var members = new RedisValue[permitCount];
        for (var i = 0; i < permitCount; i++)
        {
            members[i] = $"{leaseId}:{i + 1}";
        }

        return new ConcurrencyLease(this, members);
    }

    // In the background: the request must not wait on Redis; unreleased members expire.
    private void Release(RedisValue[] members)
    {
        Interlocked.Decrement(ref _outstandingLeases);
        RunInBackground((db, key) => db.SortedSetRemoveAsync(key, members));
    }

    // Best effort: a lost renewal admits one more request at worst.
    private void Renew(RedisValue[] members) =>
        RunInBackground((db, key) => db.ScriptEvaluateAsync(RedisScripts.ConcurrencyRenew, [key], [_leaseTimeoutMs, .. members]));

    private sealed class ConcurrencyLease : RateLimitLease
    {
        private readonly Slots _slots;

        public ConcurrencyLease(RedisConcurrencyLimiter limiter, RedisValue[] members)
        {
            _slots = new Slots(limiter, members, this);
        }

        public override bool IsAcquired => true;

        public override IEnumerable<string> MetadataNames => [];

        public override bool TryGetMetadata(string metadataName, out object? metadata)
        {
            metadata = null;
            return false;
        }

        protected override void Dispose(bool disposing)
        {
            _slots.Release();
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// A lease's slots and renewal timer. Holds the lease weakly, so an undisposed lease can be
    /// collected and its slots released instead of renewed for the life of the pod.
    /// </summary>
    private sealed class Slots
    {
        private readonly RedisConcurrencyLimiter _limiter;
        private readonly RedisValue[] _members;
        private readonly WeakReference<ConcurrencyLease> _lease;
        private readonly ITimer _renewal;
        private readonly long _acquiredAt;
        private int _released;

        public Slots(RedisConcurrencyLimiter limiter, RedisValue[] members, ConcurrencyLease lease)
        {
            _limiter = limiter;
            _members = members;
            _lease = new WeakReference<ConcurrencyLease>(lease);
            _acquiredAt = limiter.Context.TimeProvider.GetTimestamp();
            var period = limiter.Context.ConcurrencyLeaseTimeout / 3;
            _renewal = limiter.Context.TimeProvider.CreateTimer(
                static state => ((Slots)state!).OnRenewalDue(),
                this,
                period,
                period);
        }

        /// <summary>Frees the slots once.</summary>
        public void Release()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                _renewal.Dispose();
                _limiter.Release(_members);
            }
        }

        private void OnRenewalDue()
        {
            if (Volatile.Read(ref _released) != 0)
            {
                return;
            }

            if (_limiter.Context.ConcurrencyLeaseMaxDuration is { } maximum
                && _limiter.Context.TimeProvider.GetElapsedTime(_acquiredAt) >= maximum)
            {
                // Most likely abandoned; at worst admits one more.
                Release();
            }
            else if (_lease.TryGetTarget(out _))
            {
                _limiter.Renew(_members);
            }
            else
            {
                Release();
            }
        }
    }
}
