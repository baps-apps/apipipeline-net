using System.Collections.Frozen;
using System.Threading.RateLimiting;
using StackExchange.Redis;

namespace ApiPipeline.NET.RateLimiting.Redis;

/// <summary>
/// Base of the Redis limiters: one partition's limit, decided by a Lua script so every pod spends the
/// same bucket. Never queues; time is the server's.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><c>AttemptAcquire</c> cannot block on I/O, so it always answers "not acquired"; the
/// middleware then calls <c>AcquireAsync</c>.</item>
/// <item>When Redis cannot answer, <see cref="RedisFallbackBehavior"/> decides: by default the
/// partition's per-pod twin. Every exception counts except the caller's cancellation: a store
/// problem must never become a 500.</item>
/// <item>A <c>Reject</c> policy bypasses the process-wide <see cref="RedisCircuit"/>, which other
/// policies can open, and rejects only when its own call fails.</item>
/// <item>A <see cref="ReplenishingRateLimiter"/> only so the partitioned limiter's heartbeat
/// reaches the twin through <see cref="TryReplenish"/>.</item>
/// </list>
/// </remarks>
internal abstract class RedisRateLimiter : ReplenishingRateLimiter
{
    private static readonly RateLimitLease Acquired = new RedisLease(isAcquired: true, retryAfter: null);
    private static readonly RateLimitLease NotAcquired = new RedisLease(isAcquired: false, retryAfter: null);

    private static readonly RateLimitLease NotAnsweredSynchronously = new RedisLease(
        isAcquired: false,
        retryAfter: null,
        reasonPhrase: "A Redis-backed rate limiter cannot decide without I/O; call AcquireAsync.");

    private const string RejectedWhileUnavailableReason =
        "Shared rate limiting is unavailable and this policy fails closed (RedisFallbackBehavior.Reject).";

    /// <summary>Floor of a fail-closed <c>Retry-After</c>, which counts whole seconds.</summary>
    private static readonly TimeSpan MinimumUnavailableRetryAfter = TimeSpan.FromSeconds(1);

    private readonly RedisRateLimiterContext _context;
    private readonly RedisKey _key;
    private readonly string _policyName;
    private readonly int _permitLimit;
    private readonly Lazy<RateLimiter> _twin;
    private readonly RedisFallbackBehavior _fallback;
    private readonly TimeSpan _replenishmentPeriod;
    private long _lastAcquireTimestamp;
    private int _disposed;

    /// <summary>Redis's last rejection, while its retry-after lasts (<see cref="TryRejectLocally"/>).</summary>
    private Denial? _denial;

    protected RedisRateLimiter(string partitionKey, ApiPipelineRateLimitPolicy policy, RedisRateLimiterContext context)
    {
        ArgumentNullException.ThrowIfNull(partitionKey);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(context);

        _context = context;
        _key = RedisKeys.For(context.KeyPrefix, partitionKey, context.PartitionKeySecret);
        _policyName = policy.Name;
        _permitLimit = policy.PermitLimit;
        _lastAcquireTimestamp = context.TimeProvider.GetTimestamp();
        _replenishmentPeriod = TwinReplenishmentPeriod(policy);
        _fallback = context.FallbackFor(policy.Name);

        _twin = new Lazy<RateLimiter>(() => context.InMemory.Create(partitionKey, policy));
    }

    public override bool IsAutoReplenishing => false;

    /// <summary>The twin's period; zero for concurrency.</summary>
    public override TimeSpan ReplenishmentPeriod => _replenishmentPeriod;

    /// <summary>Refills the twin. Redis state needs none.</summary>
    public override bool TryReplenish() =>
        _twin.IsValueCreated && _twin.Value is ReplenishingRateLimiter twin && twin.TryReplenish();

    /// <summary>
    /// Time since the last acquire; <c>null</c> while a lease is held or the twin is in use, so an
    /// outage does not retire the twin and hand a waiting client a fresh allowance.
    /// </summary>
    public override TimeSpan? IdleDuration =>
        HasOutstandingLeases || TwinInUse
            ? null
            : _context.TimeProvider.GetElapsedTime(Interlocked.Read(ref _lastAcquireTimestamp));

    /// <summary>Whether a lease is still held (concurrency only).</summary>
    protected virtual bool HasOutstandingLeases => false;

    /// <summary>None: the counts live in Redis.</summary>
    public override RateLimiterStatistics? GetStatistics() => null;

    /// <summary>Runs this kind's script.</summary>
    /// <param name="db">The database.</param>
    /// <param name="key">This partition's key.</param>
    /// <param name="leaseId">The attempt's <see cref="NewLeaseId"/>.</param>
    /// <param name="permitCount">Permits to spend; <c>0</c> only asks.</param>
    /// <param name="cancellationToken">The caller's token.</param>
    protected abstract Task<RedisResult> ExecuteAsync(IDatabase db, RedisKey key, RedisValue leaseId, int permitCount, CancellationToken cancellationToken);

    /// <summary>The id of one acquire attempt. None by default.</summary>
    protected virtual RedisValue NewLeaseId() => RedisValue.Null;

    /// <summary>The lease for a granted permit. Plain by default; concurrency hands its slot back.</summary>
    protected virtual RateLimitLease CreateAcquiredLease(RedisValue leaseId, int permitCount) => Acquired;

    protected RedisRateLimiterContext Context => _context;

    /// <summary>Whether the twin holds a lease or unrefilled permits, and so must be kept.</summary>
    private bool TwinInUse => _twin.IsValueCreated && _twin.Value.IdleDuration is null;

    /// <summary>
    /// Runs <paramref name="command"/> on this key without awaiting it; failures are dropped and not
    /// reported to the circuit (a lost release costs one slot until its lease expires). Sent even while
    /// the circuit is open, since leases granted before it opened still release there.
    /// </summary>
    protected void RunInBackground(Func<IDatabase, RedisKey, Task> command)
    {
        try
        {
            ObserveFailure(RunAsync(command));
        }
        catch (Exception)
        {
            // A release that cannot start is dropped like one that fails.
        }
    }

    protected override RateLimitLease AttemptAcquireCore(int permitCount)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(permitCount, _permitLimit);

        // The middleware treats an acquired lease here as final, so only "no" is safe.
        _context.SynchronousAttempts?.Attempted(_policyName);
        return NotAnsweredSynchronously;
    }

    protected override async ValueTask<RateLimitLease> AcquireAsyncCore(int permitCount, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(permitCount, _permitLimit);

        // Before the circuit, so a cancelled caller does not waste its probe.
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Exchange(ref _lastAcquireTimestamp, _context.TimeProvider.GetTimestamp());
        _context.SynchronousAttempts?.AcquiringAsync();

        if (TryRejectLocally(permitCount) is { } local)
        {
            return local;
        }

        var circuit = _context.Circuit;
        if (_fallback != RedisFallbackBehavior.Reject && !circuit.TryEnter())
        {
            return await FallBackAsync(permitCount, FallbackReason.CircuitOpen, cancellationToken).ConfigureAwait(false);
        }

        RateLimitLease? lease;
        try
        {
            lease = await EvaluateAsync(permitCount, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsRedisFailure(ex, cancellationToken))
        {
            circuit.ReportFailure(ex);
            return await FallBackAsync(
                permitCount,
                ex is TimeoutException ? FallbackReason.Timeout : FallbackReason.Error,
                cancellationToken).ConfigureAwait(false);
        }

        if (lease is null)
        {
            // Still making the first connection: starting, not an outage.
            return await FallBackAsync(permitCount, FallbackReason.Connecting, cancellationToken).ConfigureAwait(false);
        }

        circuit.ReportSuccess();
        _context.ServerCheck?.RedisAnswered(_context.Connection);
        RememberDenial(lease, permitCount);
        return lease;
    }

    /// <summary>
    /// Rejects without Redis while Redis's last rejection of at least <paramref name="permitCount"/>
    /// permits still holds; null when Redis must be asked.
    /// </summary>
    /// <remarks>
    /// Stops a flood against one partition from costing a Redis call per request, which would open the
    /// circuit for everyone. Safe because a window or bucket that rejects n permits until T rejects n or
    /// more until T. Probes and concurrency rejections (no retry-after) always ask.
    /// </remarks>
    private RedisLease? TryRejectLocally(int permitCount)
    {
        if (permitCount == 0 || Volatile.Read(ref _denial) is not { } denial || permitCount < denial.PermitCount)
        {
            return null;
        }

        var remaining = denial.RetryAfter - _context.TimeProvider.GetElapsedTime(denial.Timestamp);
        return remaining > TimeSpan.Zero
            ? new RedisLease(isAcquired: false, remaining)
            : null;
    }

    private void RememberDenial(RateLimitLease lease, int permitCount)
    {
        if (permitCount > 0
            && !lease.IsAcquired
            && lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter)
            && retryAfter > TimeSpan.Zero)
        {
            Volatile.Write(ref _denial, new Denial(_context.TimeProvider.GetTimestamp(), retryAfter, permitCount));
        }
    }

    /// <summary>A rejection, timed by the monotonic clock.</summary>
    private sealed record Denial(long Timestamp, TimeSpan RetryAfter, int PermitCount);

    protected override void Dispose(bool disposing)
    {
        Volatile.Write(ref _disposed, 1);
        if (disposing && _twin.IsValueCreated)
        {
            _twin.Value.Dispose();
        }

        base.Dispose(disposing);
    }

    protected override async ValueTask DisposeAsyncCore()
    {
        Volatile.Write(ref _disposed, 1);
        if (_twin.IsValueCreated)
        {
            await _twin.Value.DisposeAsync().ConfigureAwait(false);
        }

        await base.DisposeAsyncCore().ConfigureAwait(false);
    }

    /// <summary>
    /// Whether Redis could not decide: any exception but the caller's cancellation. Not a type list:
    /// StackExchange.Redis throws types outside <see cref="RedisException"/>, and a missed one would
    /// be a 500 per request.
    /// </summary>
    private static bool IsRedisFailure(Exception exception, CancellationToken cancellationToken) =>
        !(exception is OperationCanceledException && cancellationToken.IsCancellationRequested);

    private static TimeSpan TwinReplenishmentPeriod(ApiPipelineRateLimitPolicy policy)
    {
        var window = policy.Window ?? TimeSpan.FromMinutes(1);
        return policy.Kind switch
        {
            RateLimiterKind.FixedWindow or RateLimiterKind.TokenBucket => window,
            RateLimiterKind.SlidingWindow => window / (policy.SegmentsPerWindow ?? 4),
            _ => TimeSpan.Zero,
        };
    }

    private ValueTask<RateLimitLease> FallBackAsync(int permitCount, string reason, CancellationToken cancellationToken)
    {
        _context.Metrics.Fallback(_policyName, reason);
        return _fallback switch
        {
            RedisFallbackBehavior.Allow => ValueTask.FromResult(Acquired),
            RedisFallbackBehavior.Reject => ValueTask.FromResult<RateLimitLease>(RejectWhileUnavailable()),
            _ => _twin.Value.AcquireAsync(permitCount, cancellationToken),
        };
    }

    /// <summary>
    /// A fail-closed rejection, retry-after until Redis is next asked: the rest of the circuit's
    /// cooldown, at least a second.
    /// </summary>
    private RedisLease RejectWhileUnavailable()
    {
        var untilNextAttempt = _context.Circuit.UntilNextAttempt();
        return new RedisLease(
            isAcquired: false,
            retryAfter: untilNextAttempt > MinimumUnavailableRetryAfter ? untilNextAttempt : MinimumUnavailableRetryAfter,
            reasonPhrase: RejectedWhileUnavailableReason);
    }

    // Bounded by the command timeout. Null while the first connection is slow: that costs one
    // timeout in total, not one per request.
    private async Task<RateLimitLease?> EvaluateAsync(int permitCount, CancellationToken cancellationToken)
    {
        if (_context.Connection.IsFirstConnectSlow)
        {
            return null;
        }

        var started = _context.TimeProvider.GetTimestamp();
        var connecting = _context.Connection.GetDatabaseAsync();
        var call = CallRedisAsync(connecting, permitCount, cancellationToken);
        try
        {
            var lease = await call.WaitAsync(_context.CommandTimeout, _context.TimeProvider, cancellationToken)
                .ConfigureAwait(false);
            _context.Metrics.StoreCall(_policyName, _context.TimeProvider.GetElapsedTime(started), errorType: null);
            return lease;
        }
        catch (Exception ex)
        {
            if (IsRedisFailure(ex, cancellationToken))
            {
                _context.Metrics.StoreCall(
                    _policyName,
                    _context.TimeProvider.GetElapsedTime(started),
                    ex is TimeoutException ? StoreErrorType.Timeout : ex.GetType().FullName ?? ex.GetType().Name);
            }

            // Nobody awaits the call now: release a permit Redis still grants, observe a failure.
            _ = call.ContinueWith(
                static t =>
                {
                    if (t.IsCompletedSuccessfully)
                    {
                        t.Result.Dispose();
                    }
                    else
                    {
                        _ = t.Exception;
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            if (ex is TimeoutException && !connecting.IsCompleted && _context.Connection.IsFirstConnectPending)
            {
                _context.Connection.FirstConnectWaitedOut();
                return null;
            }

            throw;
        }
    }

    private async Task<RateLimitLease> CallRedisAsync(Task<IDatabase> connecting, int permitCount, CancellationToken cancellationToken)
    {
        var leaseId = NewLeaseId();
        IDatabase db;
        try
        {
            db = await connecting.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not (RedisException or TimeoutException or ObjectDisposedException or OperationCanceledException))
        {
            // Any connection failure means the store is unavailable: fall back, not 500.
            throw new RedisException("The Redis connection could not be obtained.", ex);
        }

        var result = await ExecuteAsync(db, _key, leaseId, permitCount, cancellationToken).ConfigureAwait(false);
        return ToLease(result, leaseId, permitCount);
    }

    private async Task RunAsync(Func<IDatabase, RedisKey, Task> command)
    {
        var db = await _context.Connection.GetDatabaseAsync().ConfigureAwait(false);
        await command(db, _key).ConfigureAwait(false);
    }

    private static void ObserveFailure(Task task) =>
        _ = task.ContinueWith(
            static t => _ = t.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    private RateLimitLease ToLease(RedisResult result, RedisValue leaseId, int permitCount)
    {
        // Validated, not cast: a malformed result falls back instead of throwing a 500.
        if (result is null
            || result.Resp2Type != ResultType.Array
            || result.Length != 2
            || result[0].Resp2Type != ResultType.Integer
            || result[1].Resp2Type != ResultType.Integer)
        {
            throw new RedisException("A rate limiter script returned an unexpected result.");
        }

        if ((long)result[0] == 1)
        {
            return permitCount == 0 ? Acquired : CreateAcquiredLease(leaseId, permitCount);
        }

        var retryAfterMs = (long)result[1];
        return retryAfterMs > 0
            ? new RedisLease(isAcquired: false, TimeSpan.FromMilliseconds(retryAfterMs))
            : NotAcquired;
    }

    private sealed class RedisLease : RateLimitLease
    {
        private readonly TimeSpan? _retryAfter;
        private readonly string? _reasonPhrase;
        private readonly string[] _metadataNames;

        public RedisLease(bool isAcquired, TimeSpan? retryAfter, string? reasonPhrase = null)
        {
            IsAcquired = isAcquired;
            _retryAfter = retryAfter;
            _reasonPhrase = reasonPhrase;
            _metadataNames = (retryAfter, reasonPhrase) switch
            {
                (not null, not null) => [MetadataName.RetryAfter.Name, MetadataName.ReasonPhrase.Name],
                (not null, null) => [MetadataName.RetryAfter.Name],
                (null, not null) => [MetadataName.ReasonPhrase.Name],
                _ => [],
            };
        }

        public override bool IsAcquired { get; }

        public override IEnumerable<string> MetadataNames => _metadataNames;

        public override bool TryGetMetadata(string metadataName, out object? metadata)
        {
            if (_retryAfter is { } retryAfter && metadataName == MetadataName.RetryAfter.Name)
            {
                metadata = retryAfter;
                return true;
            }

            if (_reasonPhrase is { } reasonPhrase && metadataName == MetadataName.ReasonPhrase.Name)
            {
                metadata = reasonPhrase;
                return true;
            }

            metadata = null;
            return false;
        }
    }
}

/// <summary>What every Redis limiter of one factory shares.</summary>
internal sealed class RedisRateLimiterContext
{
    public RedisRateLimiterContext(
        RedisConnectionSource connection,
        string keyPrefix,
        TimeSpan commandTimeout,
        TimeProvider timeProvider,
        InMemoryRateLimiterFactory inMemory,
        RedisCircuit circuit,
        RedisRateLimitingMetrics metrics,
        TimeSpan concurrencyLeaseTimeout,
        RedisFallbackBehavior fallback = RedisFallbackBehavior.PerPod,
        IDictionary<string, RedisFallbackBehavior>? policyFallbacks = null,
        SynchronousAttemptMonitor? synchronousAttempts = null,
        RedisServerCheck? serverCheck = null,
        byte[]? partitionKeySecret = null,
        TimeSpan? concurrencyLeaseMaxDuration = null)
    {
        SynchronousAttempts = synchronousAttempts;
        ServerCheck = serverCheck;
        PartitionKeySecret = partitionKeySecret;
        ConcurrencyLeaseMaxDuration = concurrencyLeaseMaxDuration;
        _fallback = fallback;
        _policyFallbacks = (policyFallbacks ?? new Dictionary<string, RedisFallbackBehavior>())
            .ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
        Connection = connection;
        KeyPrefix = keyPrefix;
        CommandTimeout = commandTimeout;
        ConcurrencyLeaseTimeout = concurrencyLeaseTimeout;
        TimeProvider = timeProvider;
        InMemory = inMemory;
        Circuit = circuit;
        Metrics = metrics;
    }

    public RedisConnectionSource Connection { get; }

    public string KeyPrefix { get; }

    public TimeSpan CommandTimeout { get; }

    /// <summary>How long an unreleased concurrency lease holds its slot.</summary>
    public TimeSpan ConcurrencyLeaseTimeout { get; }

    /// <summary>The longest a concurrency lease keeps its slot; null for no limit.</summary>
    public TimeSpan? ConcurrencyLeaseMaxDuration { get; }

    /// <summary>The key-hashing secret; null for a plain hash (Development only).</summary>
    public byte[]? PartitionKeySecret { get; }

    public TimeProvider TimeProvider { get; }

    /// <summary>Builds a partition's per-pod twin.</summary>
    public InMemoryRateLimiterFactory InMemory { get; }

    /// <summary>Process-wide.</summary>
    public RedisCircuit Circuit { get; }

    public RedisRateLimitingMetrics Metrics { get; }

    /// <summary>Null when not monitored.</summary>
    public SynchronousAttemptMonitor? SynchronousAttempts { get; }

    /// <summary>Null when not checked.</summary>
    public RedisServerCheck? ServerCheck { get; }

    private readonly RedisFallbackBehavior _fallback;
    private readonly FrozenDictionary<string, RedisFallbackBehavior> _policyFallbacks;

    /// <summary>What <paramref name="policyName"/> does while Redis cannot decide.</summary>
    public RedisFallbackBehavior FallbackFor(string policyName) =>
        _policyFallbacks.TryGetValue(policyName, out var behavior) ? behavior : _fallback;
}
