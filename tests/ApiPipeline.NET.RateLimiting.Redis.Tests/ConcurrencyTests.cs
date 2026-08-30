using System.Diagnostics;
using System.Net;
using ApiPipeline.NET.RateLimiting;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using StackExchange.Redis;

namespace ApiPipeline.NET.RateLimiting.Redis.Tests;

/// <summary>
/// The shared concurrency limit: one Redis sorted set of lease ids per partition, scored by expiry,
/// so at most <c>PermitLimit</c> requests are in flight across all pods. A lease frees its slot when
/// the request ends; a lease whose pod died frees it when it expires.
/// </summary>
public sealed class ConcurrencyTests(RedisFixture redis)
{
    private const string HoldPath = "/hold";
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Two in flight on host A fill a limit of 2, so host B is rejected; once they finish, B is
    /// admitted. Per pod, B would have been admitted at once. B retries briefly: A frees its slots
    /// in the background, on another connection.
    /// </summary>
    [Fact]
    public async Task TwoHosts_ShareTheInFlightLimit()
    {
        var config = RedisTestHost.Concurrency(RedisTestHost.UniquePrefix(), permitLimit: 2);
        var gate = new InFlightGate();
        await using var hostA = await RedisTestHost.CreateAsync(redis.ConnectionString, config, configureApp: gate.Map);
        await using var hostB = await RedisTestHost.CreateAsync(redis.ConnectionString, config);

        Task<HttpStatusCode> first, second;
        try
        {
            first = hostA.GetStatusAsync(path: HoldPath);
            second = hostA.GetStatusAsync(path: HoldPath);
            await gate.WaitUntilInFlightAsync(2);

            (await hostB.GetStatusAsync()).Should().Be(HttpStatusCode.TooManyRequests, "host A holds both slots");
        }
        finally
        {
            gate.Open();
        }

        (await first).Should().Be(HttpStatusCode.OK);
        (await second).Should().Be(HttpStatusCode.OK);
        (await StatusOnceAdmittedAsync(hostB)).Should().Be(HttpStatusCode.OK, "host A's requests ended and freed their slots");
    }

    /// <summary>
    /// A finished request removes its lease from Redis: the slot is free long before the 300 s lease
    /// timeout, and requests one after another all pass a limit of 1.
    /// </summary>
    [Fact]
    public async Task ReleasedLease_FreesTheSlot()
    {
        var prefix = RedisTestHost.UniquePrefix();
        var config = RedisTestHost.Concurrency(prefix, permitLimit: 1);
        var gate = new InFlightGate();
        await using var app = await RedisTestHost.CreateAsync(redis.ConnectionString, config, configureApp: gate.Map);

        Task<HttpStatusCode> held;
        RedisKey key;
        try
        {
            held = app.GetStatusAsync(path: HoldPath);
            await gate.WaitUntilInFlightAsync(1);

            key = await SingleKeyAsync(prefix);
            (await redis.Multiplexer.GetDatabase().SortedSetLengthAsync(key)).Should().Be(1, "Redis holds the in-flight lease");
            (await app.GetStatusAsync()).Should().Be(HttpStatusCode.TooManyRequests, "the only slot is taken");
        }
        finally
        {
            gate.Open();
        }

        (await held).Should().Be(HttpStatusCode.OK);
        await WaitUntilAsync(async () => await redis.Multiplexer.GetDatabase().SortedSetLengthAsync(key) == 0);

        // Same host, same connection: each release reaches Redis before the next acquire.
        (await app.GetStatusAsync()).Should().Be(HttpStatusCode.OK);
        (await app.GetStatusAsync()).Should().Be(HttpStatusCode.OK, "the previous request returned its slot");
        (await app.GetStatusAsync()).Should().Be(HttpStatusCode.OK, "the previous request returned its slot");
    }

    /// <summary>
    /// A pod that dies mid-request neither releases nor renews its lease, so the slot frees once its
    /// lease timeout passes. Simulated by writing what a dead pod leaves behind (a member with a
    /// minute's expiry that nothing renews), then moving that expiry into the past, so a slow machine
    /// cannot let it lapse before the first check.
    /// </summary>
    [Fact]
    public async Task CrashedPodLease_ExpiresAfterTimeout()
    {
        var prefix = RedisTestHost.UniquePrefix();
        var config = RedisTestHost.Concurrency(prefix, permitLimit: 1);
        await using var app = await RedisTestHost.CreateAsync(redis.ConnectionString, config);
        var factory = app.Services.GetRequiredService<IApiPipelineRateLimiterFactory>();
        var policy = new ApiPipelineRateLimitPolicy { Name = "strict", Kind = RateLimiterKind.Concurrency, PermitLimit = 1, QueueLimit = 0 };
        using var livePod = factory.Create("partition", policy);
        var key = RedisKeys.For(prefix, "partition");

        // The dead pod's lease: one permit, 60 s timeout, never renewed.
        await redis.Multiplexer.GetDatabase().ScriptEvaluateAsync(
            RedisScripts.ConcurrencyAcquire, [key], [1, 60_000, "dead-pod", 1]);

        using (var rejected = await livePod.AcquireAsync(1, TestContext.Current.CancellationToken))
        {
            rejected.IsAcquired.Should().BeFalse("the crashed pod's lease still holds the only slot");
        }

        // Its timeout passes: the member's expiry (its score) is now in the past.
        await redis.Multiplexer.GetDatabase().SortedSetAddAsync(key, "dead-pod:1", 1, SortedSetWhen.Exists);

        using var admitted = await livePod.AcquireAsync(1, TestContext.Current.CancellationToken);
        admitted.IsAcquired.Should().BeTrue("the orphaned lease expired");
    }

    /// <summary>
    /// A lease held longer than the lease timeout keeps its slot: it is renewed every third of the
    /// timeout. Without renewal the slot would expire mid-request and a second request be admitted.
    /// </summary>
    /// <remarks>
    /// Deterministic, with no wait on the real clock: the renewal timer runs on a fake clock, and
    /// before each renewal the lease's expiry in Redis is backdated, as if its timeout had run out on
    /// Redis's clock. Each renewal must push the expiry back into the future, three times over (one
    /// whole timeout), and the slot must still be held at the end.
    /// </remarks>
    [Fact]
    public async Task LongRunningLease_IsRenewed_AndKeepsItsSlot()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new FakeTimeProvider();
        var (provider, context) = RedisTestHost.CreateLimiterContext(_ => redis.Multiplexer, timeProvider: clock);
        await using var _ = provider;
        var policy = new ApiPipelineRateLimitPolicy { Name = "strict", Kind = RateLimiterKind.Concurrency, PermitLimit = 1, QueueLimit = 0 };
        using var podA = new RedisConcurrencyLimiter(RedisTestHost.LimiterPartition, policy, context);
        using var podB = new RedisConcurrencyLimiter(RedisTestHost.LimiterPartition, policy, context);
        var db = redis.Multiplexer.GetDatabase();
        var key = RedisKeys.For(context.KeyPrefix, RedisTestHost.LimiterPartition, context.PartitionKeySecret);

        using (var held = await podA.AcquireAsync(1, ct))
        {
            held.IsAcquired.Should().BeTrue();

            for (var renewal = 1; renewal <= 3; renewal++)
            {
                // An expiry of 1 ms after the epoch is long past: without a renewal, the next
                // acquire would prune the lease and admit a second request.
                var member = (await OnlyLeaseAsync(db, key)).Element;
                await db.SortedSetAddAsync(key, member, 1, SortedSetWhen.Exists);

                clock.Advance(context.ConcurrencyLeaseTimeout / 3);

                await WaitUntilAsync(
                    async () => (await OnlyLeaseAsync(db, key)).Score > 1,
                    $"renewal {renewal} did not extend the held lease's expiry in Redis");
            }

            using var second = await podB.AcquireAsync(1, ct);
            second.IsAcquired.Should().BeFalse("the held lease was renewed past its whole timeout");
        }

        // Released: the slot frees at once, without waiting for an expiry.
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < Deadline)
        {
            using var next = await podB.AcquireAsync(1, TestContext.Current.CancellationToken);
            if (next.IsAcquired)
            {
                return;
            }

            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        throw new Xunit.Sdk.XunitException("the released lease's slot was not freed");
    }

    /// <summary>
    /// The framework retires a partition's limiter once it reports itself idle. A limiter holding a
    /// lease must not: retiring it would drop the per-pod twin's count, and a released lease would
    /// release through a disposed limiter. Idle again once the lease is disposed.
    /// </summary>
    [Fact]
    public async Task OutstandingLease_KeepsTheLimiterBusy()
    {
        var config = RedisTestHost.Concurrency(RedisTestHost.UniquePrefix(), permitLimit: 2);
        await using var app = await RedisTestHost.CreateAsync(redis.ConnectionString, config);
        var factory = app.Services.GetRequiredService<IApiPipelineRateLimiterFactory>();
        using var limiter = factory.Create(
            "partition",
            new ApiPipelineRateLimitPolicy { Name = "strict", Kind = RateLimiterKind.Concurrency, PermitLimit = 2, QueueLimit = 0 });

        var lease = await limiter.AcquireAsync(1, TestContext.Current.CancellationToken);
        lease.IsAcquired.Should().BeTrue();
        limiter.IdleDuration.Should().BeNull("a lease is outstanding");

        lease.Dispose();
        lease.Dispose();

        limiter.IdleDuration.Should().NotBeNull("the only lease was released (twice, harmlessly)");
    }

    /// <summary>
    /// With Redis unreachable, the per-pod twin decides, and its lease is the one handed out: ending
    /// the request frees the twin's slot (and keeps the limiter busy until then), just as per pod.
    /// </summary>
    [Fact]
    public async Task FallbackLease_ReleasesThePerPodTwin()
    {
        var config = RedisTestHost.Concurrency(RedisTestHost.UniquePrefix(), permitLimit: 1);
        await using var app = await RedisTestHost.CreateAsync("127.0.0.1:1,connectTimeout=200", config);
        var factory = app.Services.GetRequiredService<IApiPipelineRateLimiterFactory>();
        using var limiter = factory.Create(
            "partition",
            new ApiPipelineRateLimitPolicy { Name = "strict", Kind = RateLimiterKind.Concurrency, PermitLimit = 1, QueueLimit = 0 });

        var lease = await limiter.AcquireAsync(1, TestContext.Current.CancellationToken);
        lease.IsAcquired.Should().BeTrue("the per-pod twin decides");
        limiter.IdleDuration.Should().BeNull("the twin's lease is outstanding");
        using (var rejected = await limiter.AcquireAsync(1, TestContext.Current.CancellationToken))
        {
            rejected.IsAcquired.Should().BeFalse("the twin's only slot is taken");
        }

        lease.Dispose();

        using var again = await limiter.AcquireAsync(1, TestContext.Current.CancellationToken);
        again.IsAcquired.Should().BeTrue("disposing the lease released the twin");
    }

    /// <summary>Sends <c>GET /test</c> until it is admitted or the deadline passes; returns the last status.</summary>
    private static async Task<HttpStatusCode> StatusOnceAdmittedAsync(WebApplication app)
    {
        var elapsed = Stopwatch.StartNew();
        HttpStatusCode status;
        while ((status = await app.GetStatusAsync()) != HttpStatusCode.OK && elapsed.Elapsed < Deadline)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        return status;
    }

    private static Task WaitUntilAsync(Func<Task<bool>> condition) =>
        WaitUntilAsync(condition, "the condition should hold well before the deadline");

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, string because)
    {
        var elapsed = Stopwatch.StartNew();
        while (!await condition())
        {
            elapsed.Elapsed.Should().BeLessThan(Deadline, because);
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
    }

    /// <summary>The single lease member of a one-permit concurrency key, with its expiry (score).</summary>
    private static async Task<SortedSetEntry> OnlyLeaseAsync(IDatabase db, RedisKey key)
    {
        var members = await db.SortedSetRangeByRankWithScoresAsync(key);
        members.Should().ContainSingle("one lease of one permit is held");
        return members[0];
    }

    private async Task<RedisKey> SingleKeyAsync(string prefix)
    {
        var keys = new List<RedisKey>();
        await foreach (var key in redis.Multiplexer.GetServers()[0].KeysAsync(pattern: prefix + "*"))
        {
            keys.Add(key);
        }

        keys.Should().ContainSingle("one partition is in use");
        return keys[0];
    }

    /// <summary>
    /// <c>GET /hold</c>: a request that stays in flight until <see cref="Open"/>, so a test controls
    /// exactly when its lease is held and when it is released.
    /// </summary>
    private sealed class InFlightGate
    {
        private readonly TaskCompletionSource _open = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly SemaphoreSlim _entered = new(0);

        public void Map(WebApplication app) =>
            app.MapGet(HoldPath, async () =>
            {
                _entered.Release();
                await _open.Task;
                return Results.Ok("held");
            });

        /// <summary>Waits until <paramref name="count"/> requests are inside the endpoint, holding leases.</summary>
        public async Task WaitUntilInFlightAsync(int count)
        {
            for (var i = 0; i < count; i++)
            {
                (await _entered.WaitAsync(Deadline, TestContext.Current.CancellationToken))
                    .Should().BeTrue("the request should reach the endpoint");
            }
        }

        public void Open() => _open.TrySetResult();
    }

    /// <summary>
    /// A lease Redis granted is released there even if the circuit opened meanwhile; otherwise its
    /// slot stays taken on every pod until the lease timeout.
    /// </summary>
    [Fact]
    public async Task LeaseGrantedBeforeCircuitOpened_IsStillReleasedInRedis()
    {
        var ct = TestContext.Current.CancellationToken;
        var (provider, context) = RedisTestHost.CreateLimiterContext(_ => redis.Multiplexer);
        await using var _ = provider;
        var policy = new ApiPipelineRateLimitPolicy { Name = "strict", Kind = RateLimiterKind.Concurrency, PermitLimit = 1, QueueLimit = 0 };
        using var limiter = new RedisConcurrencyLimiter(RedisTestHost.LimiterPartition, policy, context);
        var key = RedisKeys.For(context.KeyPrefix, RedisTestHost.LimiterPartition);

        var lease = await limiter.AcquireAsync(1, ct);
        lease.IsAcquired.Should().BeTrue();
        context.Circuit.ReportFailure(new TimeoutException("brownout"));
        context.Circuit.IsOpen.Should().BeTrue();

        lease.Dispose();

        var deadline = Stopwatch.StartNew();
        while (await redis.Multiplexer.GetDatabase().SortedSetLengthAsync(key) > 0 && deadline.Elapsed < TimeSpan.FromSeconds(5))
        {
            await Task.Delay(20, ct);
        }

        (await redis.Multiplexer.GetDatabase().SortedSetLengthAsync(key)).Should().Be(0, "the slot was released in Redis");
    }
}
