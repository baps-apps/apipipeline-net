using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace ApiPipeline.NET.RateLimiting.Redis;

/// <summary>
/// Whether the process uses Redis. Opens after <c>FailureThreshold</c> failures in a row, or a
/// failure share of <c>FailureRatio</c> over <c>SamplingDuration</c>; while open, non-<c>Reject</c>
/// policies fall back without trying Redis. After each cooldown one probe call tries Redis.
/// </summary>
/// <remarks>
/// Any success resets the consecutive count; a <c>WRONGTYPE</c> reply counts as an answer, so one
/// broken key never opens it. Logs once per transition. A success from a call started before
/// opening is ignored until the first cooldown, so a partial outage cannot flap it.
/// </remarks>
internal sealed class RedisCircuit
{
    /// <summary>Start of the Warning logged when it opens.</summary>
    internal const string UnavailableMessage = "Shared rate limiting is unavailable until Redis responds.";

    /// <summary>The Information logged when it closes.</summary>
    internal const string RestoredMessage = "Shared rate limiting restored; limits are shared across pods again.";

    private readonly TimeProvider _timeProvider;
    private readonly ILogger<RedisCircuit> _logger;
    private readonly TimeSpan _cooldown;
    private readonly int _failureThreshold;
    private readonly string _fallbacks;
    private readonly Lock _gate = new();

    // SamplingDuration in Buckets slices, reused round-robin; counting is lock-free.
    private const int Buckets = 10;
    private readonly Bucket[] _buckets = [.. Enumerable.Range(0, Buckets).Select(_ => new Bucket())];
    private readonly Lock _windowGate = new();
    private readonly long _bucketTicks;
    private readonly double _failureRatio;
    private readonly int _minimumThroughput;

    private volatile bool _open;

    private int _consecutiveFailures;

    // Written under _gate.
    private long _openedAt;

    // Written under _gate; read without it on the open path, so a cooldown turns callers away lock-free.
    private long _lastAttemptAt;

    public RedisCircuit(
        TimeProvider timeProvider,
        ILogger<RedisCircuit> logger,
        IOptions<ApiPipelineRedisRateLimitingOptions> options)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(options);

        _timeProvider = timeProvider;
        _logger = logger;
        _cooldown = options.Value.FallbackCooldown;
        _failureThreshold = Math.Max(1, options.Value.FailureThreshold);
        _failureRatio = options.Value.FailureRatio;
        _minimumThroughput = Math.Max(1, options.Value.MinimumThroughput);
        _fallbacks = DescribeFallbacks(options.Value);
        _bucketTicks = Math.Max(
            1,
            (long)(options.Value.SamplingDuration.TotalSeconds * timeProvider.TimestampFrequency / Buckets));
    }

    public bool IsOpen => _open;

    /// <summary>Time until Redis is next asked: the rest of the cooldown while open, zero while closed.</summary>
    public TimeSpan UntilNextAttempt()
    {
        if (!_open)
        {
            return TimeSpan.Zero;
        }

        var remaining = _cooldown - _timeProvider.GetElapsedTime(Volatile.Read(ref _lastAttemptAt));
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    /// <summary>Whether this call may use Redis: always while closed; while open, only the probe.</summary>
    /// <remarks>
    /// While open, a caller inside the cooldown returns without the lock; only callers that find the
    /// cooldown over take it, and the first of them becomes the probe.
    /// </remarks>
    public bool TryEnter()
    {
        if (!_open)
        {
            return true;
        }

        if (_timeProvider.GetElapsedTime(Volatile.Read(ref _lastAttemptAt)) < _cooldown)
        {
            return !_open;
        }

        lock (_gate)
        {
            if (!_open)
            {
                return true;
            }

            if (_timeProvider.GetElapsedTime(_lastAttemptAt) < _cooldown)
            {
                return false;
            }

            Volatile.Write(ref _lastAttemptAt, _timeProvider.GetTimestamp());
            return true;
        }
    }

    /// <summary>Redis answered: resets the count, and closes the circuit after a cooldown.</summary>
    public void ReportSuccess()
    {
        if (!_open)
        {
            Interlocked.Increment(ref CurrentBucket(_timeProvider.GetTimestamp()).Successes);

            // Read first: no shared-cache-line write on the healthy path.
            if (Volatile.Read(ref _consecutiveFailures) != 0)
            {
                Interlocked.Exchange(ref _consecutiveFailures, 0);
            }

            return;
        }

        lock (_gate)
        {
            if (!_open || _timeProvider.GetElapsedTime(_openedAt) < _cooldown)
            {
                return;
            }

            _open = false;
        }

        _logger.SharedRateLimitingRestored();
    }

    /// <summary>Redis failed: may open the circuit; a failed probe keeps it open another cooldown.</summary>
    public void ReportFailure(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        int failures;
        bool opened;
        lock (_gate)
        {
            var now = _timeProvider.GetTimestamp();
            Volatile.Write(ref _lastAttemptAt, now);
            if (_open)
            {
                return;
            }

            var bucket = CurrentBucket(now);
            if (IsKeyError(exception))
            {
                Interlocked.Increment(ref bucket.Successes);
            }
            else
            {
                Interlocked.Increment(ref bucket.Failures);
            }

            failures = Interlocked.Increment(ref _consecutiveFailures);
            opened = failures >= _failureThreshold || FailureRateReached(now);
            if (opened)
            {
                _consecutiveFailures = 0;
                _openedAt = now;
                _open = true;
                ClearWindow();
            }
        }

        if (opened)
        {
            _logger.SharedRateLimitingUnavailable(exception, _fallbacks);
        }
        else
        {
            _logger.SharedRateLimitingCallFailed(exception, failures, _failureThreshold);
        }
    }

    /// <summary>One sentence per fallback in use, so the warning says what each actually does.</summary>
    internal static string DescribeFallbacks(ApiPipelineRedisRateLimitingOptions options)
    {
        var inUse = new HashSet<RedisFallbackBehavior> { options.Fallback };
        inUse.UnionWith((options.PolicyFallbacks ?? new Dictionary<string, RedisFallbackBehavior>()).Values);

        var sentences = new List<string>(3);
        if (inUse.Contains(RedisFallbackBehavior.PerPod))
        {
            sentences.Add(
                "Policies that fall back per pod (PerPod) are enforced on each pod alone, so with N replicas " +
                "a caller can get N times the limit.");
        }

        if (inUse.Contains(RedisFallbackBehavior.Allow))
        {
            sentences.Add("Policies that fall back to Allow are not limited at all.");
        }

        if (inUse.Contains(RedisFallbackBehavior.Reject))
        {
            sentences.Add(
                "Fail-closed policies (Reject) still ask Redis on every request, and reject a request when " +
                "its own call fails.");
        }

        return string.Join(" ", sentences);
    }

    /// <summary><c>WRONGTYPE</c>: one bad key, not a server problem.</summary>
    private static bool IsKeyError(Exception exception) =>
        exception is RedisServerException { Kind: RedisErrorKind.WrongType };

    private bool FailureRateReached(long now)
    {
        var current = now / _bucketTicks;
        long successes = 0;
        long failures = 0;
        foreach (var bucket in _buckets)
        {
            var epoch = Volatile.Read(ref bucket.Epoch);
            if (epoch != long.MinValue && current - epoch < Buckets)
            {
                successes += Volatile.Read(ref bucket.Successes);
                failures += Volatile.Read(ref bucket.Failures);
            }
        }

        var total = successes + failures;
        return total >= _minimumThroughput && failures >= _failureRatio * total;
    }

    /// <summary>The slice for <paramref name="now"/>, reset if it holds an older period.</summary>
    private Bucket CurrentBucket(long now)
    {
        var epoch = now / _bucketTicks;
        var bucket = _buckets[(int)(epoch % Buckets)];
        if (Volatile.Read(ref bucket.Epoch) != epoch)
        {
            lock (_windowGate)
            {
                if (bucket.Epoch != epoch)
                {
                    bucket.Successes = 0;
                    bucket.Failures = 0;
                    Volatile.Write(ref bucket.Epoch, epoch);
                }
            }
        }

        return bucket;
    }

    /// <summary>Forgets counted calls, so the brownout that opened it is not counted twice.</summary>
    private void ClearWindow()
    {
        lock (_windowGate)
        {
            foreach (var bucket in _buckets)
            {
                bucket.Successes = 0;
                bucket.Failures = 0;
                bucket.Epoch = long.MinValue;
            }
        }
    }

    /// <summary>One slice of the failure-rate window; fields for <c>Interlocked</c>.</summary>
    private sealed class Bucket
    {
        public long Epoch = long.MinValue;
        public int Successes;
        public int Failures;
    }
}
