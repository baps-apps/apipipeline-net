using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using ApiPipeline.NET.Observability;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using StackExchange.Redis;

namespace ApiPipeline.NET.RateLimiting.Redis.Tests;

/// <summary>
/// When Redis cannot answer, every request is decided by today's per-pod limiter instead: nobody is
/// blocked by the outage and nobody is unlimited. One warning when sharing stops, one information
/// record when it resumes, nothing per request.
/// </summary>
public sealed class FallbackTests(RedisFixture redis)
{
    /// <summary>
    /// The circuit is process-wide: timeouts on a CPU-starved pod, or failures of other policies,
    /// open it for everyone. A fail-closed policy must not turn that into a 429 for every caller for
    /// a whole cooldown while Redis answers fine. It keeps asking Redis (each call is bounded by
    /// <c>CommandTimeout</c>) and rejects only when its own call fails.
    /// </summary>
    [Fact]
    public async Task RejectPolicy_WithTheCircuitOpenedByOtherFailures_StillAsksRedis()
    {
        var config = RedisTestHost.FixedWindow(RedisTestHost.UniquePrefix(), permitLimit: 2);
        config["ApiPipeline:RateLimiting:Redis:Fallback"] = "Reject";
        await using var app = await RedisTestHost.CreateAsync(redis.ConnectionString, config);
        var circuit = app.Services.GetRequiredService<RedisCircuit>();
        circuit.ReportFailure(new TimeoutException("test: a starved pod's call timed out"));
        circuit.IsOpen.Should().BeTrue("the test host opens the circuit on the first failure");

        var statuses = new[] { await app.GetStatusAsync(), await app.GetStatusAsync(), await app.GetStatusAsync() };

        statuses.Should().Equal(
            [HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests],
            "Redis decides: two permits, then the limit, not a blanket fail-closed 429");
    }

    /// <summary>A policy that does not fail closed still falls back while the circuit is open.</summary>
    [Fact]
    public async Task AllowPolicy_WithTheCircuitOpen_FallsBackWithoutAskingRedis()
    {
        var config = RedisTestHost.FixedWindow(RedisTestHost.UniquePrefix(), permitLimit: 1);
        config["ApiPipeline:RateLimiting:Redis:Fallback"] = "Allow";
        await using var app = await RedisTestHost.CreateAsync(redis.ConnectionString, config);
        app.Services.GetRequiredService<RedisCircuit>().ReportFailure(new TimeoutException("test"));

        var statuses = new[] { await app.GetStatusAsync(), await app.GetStatusAsync() };

        statuses.Should().Equal([HttpStatusCode.OK, HttpStatusCode.OK], "Allow admits while the circuit is open");
    }

    private const string UnavailableMessage = "Shared rate limiting is unavailable";
    private const string RestoredMessage = "Shared rate limiting restored";
    private const string FallbackInstrument = "apipipeline.rate_limiting.store.fallback";
    private const string DegradedInstrument = "apipipeline.rate_limiting.store.degraded";
    private const string StoreDurationInstrument = "apipipeline.rate_limiting.store.duration";

    /// <summary>
    /// Redis down: the per-pod limit still applies (2 of 5 pass, not 5 of 5 and not 0 of 5), and the
    /// outage is logged once, not once per request.
    /// </summary>
    [Fact]
    public async Task Unreachable_FallsBackToPerPodLimits_AndWarnsOnce()
    {
        var logs = new CapturingLoggerProvider();
        var config = RedisTestHost.FixedWindow(RedisTestHost.UniquePrefix(), permitLimit: 2);
        await using var app = await RedisTestHost.CreateAsync("127.0.0.1:1,connectTimeout=200", config, logs: logs);

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 5; i++)
        {
            statuses.Add(await app.GetStatusAsync());
        }

        statuses.Count(s => s == HttpStatusCode.OK).Should().Be(2, "the per-pod limit applies while Redis is down");
        statuses.Count(s => s == HttpStatusCode.TooManyRequests).Should().Be(3);
        Snapshot(logs).Count(m => m.Contains(UnavailableMessage, StringComparison.Ordinal)).Should().Be(1);
    }

    /// <summary>
    /// A real outage of a working Redis: sharing stops (gauge 1) and, once Redis answers again and the
    /// cooldown has passed, resumes (gauge 0) with one information record.
    /// </summary>
    [Fact]
    public async Task Outage_ThenRecovery_LogsRestoredAndClearsGauge()
    {
        var logs = new CapturingLoggerProvider();
        var config = RedisTestHost.FixedWindow(RedisTestHost.UniquePrefix(), permitLimit: 100);
        config["ApiPipeline:RateLimiting:Redis:FallbackCooldown"] = "00:00:01";
        await using var app = await RedisTestHost.CreateAsync(redis.ConnectionString, config, logs: logs);

        // Connected and shared before the outage.
        (await app.GetStatusAsync()).Should().Be(HttpStatusCode.OK);
        ReadDegraded(app).Should().Be(0);

        await redis.Container.PauseAsync(TestContext.Current.CancellationToken);
        try
        {
            (await app.GetStatusAsync()).Should().Be(HttpStatusCode.OK, "the per-pod limit decides during the outage");
            ReadDegraded(app).Should().Be(1);
            Snapshot(logs).Should().Contain(m => m.Contains(UnavailableMessage, StringComparison.Ordinal));
        }
        finally
        {
            await redis.Container.UnpauseAsync(TestContext.Current.CancellationToken);
        }

        // After the 1 s cooldown one request probes Redis; a success closes the circuit. Polled until
        // a deadline rather than slept for a fixed time: when the probe happens, and how long the
        // client takes to resume after the pause, depend on the machine. Every 250 ms keeps the
        // requests (at most 60) inside the per-pod limit of 100.
        var deadline = Stopwatch.StartNew();
        do
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250), TestContext.Current.CancellationToken);
            (await app.GetStatusAsync()).Should().Be(HttpStatusCode.OK);
        }
        while (!Snapshot(logs).Any(m => m.Contains(RestoredMessage, StringComparison.Ordinal))
            && deadline.Elapsed < TimeSpan.FromSeconds(15));

        Snapshot(logs).Count(m => m.Contains(RestoredMessage, StringComparison.Ordinal)).Should().Be(1);
        ReadDegraded(app).Should().Be(0);
    }

    /// <summary>
    /// While the circuit is open nobody waits on Redis: once a failure has opened it, 20 more
    /// requests never ask for the connection again (a failed connection is otherwise retried on
    /// every call). The clock is fake and stands still, so the
    /// cooldown cannot run out mid-test however slow the machine is.
    /// </summary>
    [Fact]
    public async Task WhileOpen_RedisIsNotCalled()
    {
        var clock = new FakeTimeProvider();
        var attempts = 0;
        var config = RedisTestHost.FixedWindow(RedisTestHost.UniquePrefix(), permitLimit: 1000);
        await using var app = await RedisTestHost.CreateAsync(
            redis.ConnectionString,
            config,
            configureServices: (services, configuration) =>
            {
                services.AddSingleton<TimeProvider>(clock);
                services.AddApiPipelineRedisRateLimiting(configuration, _ =>
                {
                    Interlocked.Increment(ref attempts);
                    throw new RedisException("test: never connects");
                });
            });

        (await app.GetStatusAsync()).Should().Be(HttpStatusCode.OK, "the first failure falls back");
        app.Services.GetRequiredService<RedisCircuit>().IsOpen.Should().BeTrue();
        var attemptsWhenOpened = Volatile.Read(ref attempts);

        for (var i = 0; i < 20; i++)
        {
            (await app.GetStatusAsync()).Should().Be(HttpStatusCode.OK);
        }

        Volatile.Read(ref attempts).Should().Be(attemptsWhenOpened, "with the circuit open no request tries Redis");
    }

    /// <summary>
    /// A connection source that throws something other than a Redis exception (a factory bug, or a
    /// factory that returns null) is still a store failure, not a 500: the per-pod limit decides
    /// (2 of 5 pass), with one Warning that carries the cause.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConnectionFactoryFailure_FallsBackPerPod_AndWarnsOnce(bool returnsNull)
    {
        var logs = new CapturingLoggerProvider();
        var config = RedisTestHost.FixedWindow(RedisTestHost.UniquePrefix(), permitLimit: 2);
        await using var app = await RedisTestHost.CreateAsync(
            redis.ConnectionString,
            config,
            logs: logs,
            configureServices: (services, configuration) =>
                services.AddApiPipelineRedisRateLimiting(
                    configuration,
                    _ => returnsNull ? null! : throw new InvalidOperationException("test: factory bug")));

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 5; i++)
        {
            statuses.Add(await app.GetStatusAsync());
        }

        statuses.Should().NotContain(HttpStatusCode.InternalServerError);
        statuses.Count(s => s == HttpStatusCode.OK).Should().Be(2, "the per-pod limit applies");
        statuses.Count(s => s == HttpStatusCode.TooManyRequests).Should().Be(3);
        var warnings = Snapshot(logs).Where(m => m.Contains(UnavailableMessage, StringComparison.Ordinal)).ToList();
        warnings.Should().ContainSingle().Which.Should().Contain(
            returnsNull ? "AddApiPipelineRedisRateLimiting(configuration, sp => ...)" : "test: factory bug",
            "the Warning carries the cause");
    }

    /// <summary>
    /// The per-pod limiter that decides during an outage refills: it runs no timer of its own, and
    /// the framework's partitioned limiter replenishes it through the Redis limiter that wraps it.
    /// Without that, a client that spent its limit would get 429 for the whole outage.
    /// </summary>
    [Theory]
    [InlineData("FixedWindow")]
    [InlineData("SlidingWindow")]
    [InlineData("TokenBucket")]
    public async Task Unreachable_PerPodLimitStillRefills(string kind)
    {
        var prefix = RedisTestHost.UniquePrefix();
        var config = kind switch
        {
            "SlidingWindow" => RedisTestHost.SlidingWindow(prefix, permitLimit: 2, windowSeconds: 1, segmentsPerWindow: 2),
            "TokenBucket" => RedisTestHost.TokenBucket(prefix, permitLimit: 2, tokensPerPeriod: 2, windowSeconds: 1),
            _ => RedisTestHost.FixedWindow(prefix, permitLimit: 2, windowSeconds: 1),
        };
        await using var app = await RedisTestHost.CreateAsync("127.0.0.1:1,connectTimeout=200", config);

        var exhausted = false;
        for (var i = 0; i < 10 && !exhausted; i++)
        {
            exhausted = await app.GetStatusAsync() == HttpStatusCode.TooManyRequests;
        }

        exhausted.Should().BeTrue("the per-pod limit of 2 is spent");

        // Refills after the 1 s window. Polled until a deadline, not slept for a fixed time: the
        // framework's replenishment heartbeat and the machine's load decide the exact moment. A
        // rejected request spends no permit, so polling does not delay the refill.
        var refill = Stopwatch.StartNew();
        HttpStatusCode status;
        while ((status = await app.GetStatusAsync()) != HttpStatusCode.OK && refill.Elapsed < TimeSpan.FromSeconds(10))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);
        }

        status.Should().Be(HttpStatusCode.OK, "the per-pod limit refilled after the window");
    }

    /// <summary>
    /// A pod starting up is not an outage. While the very first connection is still being made,
    /// requests that cannot wait for it are decided per pod without opening the circuit or logging;
    /// once it is made, limits are shared at once rather than after a cooldown.
    /// </summary>
    [Fact]
    public async Task SlowFirstConnect_FallsBackQuietly_ThenShares()
    {
        var logs = new CapturingLoggerProvider();
        var prefix = RedisTestHost.UniquePrefix();
        var config = RedisTestHost.FixedWindow(prefix, permitLimit: 100);
        config["ApiPipeline:RateLimiting:Redis:CommandTimeout"] = "00:00:00.250";
        // The first connect completes only when the test releases it, not after a fixed sleep the
        // two requests would have to beat.
        using var release = new ManualResetEventSlim();
        using var connected = new ManualResetEventSlim();
        await using var app = await RedisTestHost.CreateAsync(
            redis.ConnectionString,
            config,
            logs: logs,
            configureServices: (services, configuration) =>
                services.AddApiPipelineRedisRateLimiting(configuration, _ =>
                {
                    release.Wait();
                    connected.Set();
                    return redis.Multiplexer;
                }));

        var fallbacks = 0L;
        var reasons = new List<string?>();
        using var listener = Listen<long>(app, FallbackInstrument, (value, tags) =>
        {
            Interlocked.Add(ref fallbacks, value);
            foreach (var tag in tags)
            {
                if (tag.Key == "apipipeline.rate_limiting.store.fallback_reason")
                {
                    lock (reasons)
                    {
                        reasons.Add(tag.Value as string);
                    }
                }
            }
        });

        try
        {
            (await app.GetStatusAsync()).Should().Be(HttpStatusCode.OK, "the per-pod limit decides while connecting");
            (await app.GetStatusAsync()).Should().Be(HttpStatusCode.OK);
            Interlocked.Read(ref fallbacks).Should().Be(2, "each is counted as a fallback");
            lock (reasons)
            {
                reasons.Should().Equal(["connecting", "connecting"], "the pod is still making its first connection");
            }
            Snapshot(logs).Should().NotContain(m => m.Contains(UnavailableMessage, StringComparison.Ordinal), "starting is not an outage");
            app.Services.GetRequiredService<RedisCircuit>().IsOpen.Should().BeFalse();
        }
        finally
        {
            release.Set();
        }

        connected.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken).Should().BeTrue();

        (await app.GetStatusAsync()).Should().Be(HttpStatusCode.OK);
        Interlocked.Read(ref fallbacks).Should().Be(2, "once connected, Redis decides");
        var server = redis.Multiplexer.GetServers()[0];
        server.Keys(pattern: $"{prefix}:apipipeline:rl:*").Should().ContainSingle("the request was counted in Redis");
        Snapshot(logs).Should().NotContain(m => m.Contains(UnavailableMessage, StringComparison.Ordinal));
    }

    /// <summary>
    /// A first connect that hangs (Redis down at pod start: up to the client's 5 s
    /// <c>connectTimeout</c>) costs one command timeout, not one per request. The first request
    /// waits it out; once it has, the rest fall back at once until the connect settles. The clock
    /// is fake, so a request that waited would never complete.
    /// </summary>
    [Fact]
    public async Task SlowFirstConnect_OnlyTheFirstRequestWaits()
    {
        var clock = new FakeTimeProvider();
        using var release = new ManualResetEventSlim();
        var (provider, context) = RedisTestHost.CreateLimiterContext(
            _ =>
            {
                release.Wait();
                return redis.Multiplexer;
            },
            timeProvider: clock);
        await using var _ = provider;
        using var limiter = new RedisFixedWindowRateLimiter(RedisTestHost.LimiterPartition, RedisTestHost.LimiterPolicy, context);

        try
        {
            var first = limiter.AcquireAsync(1, TestContext.Current.CancellationToken).AsTask();
            first.IsCompleted.Should().BeFalse("the first request waits for the connect, up to the command timeout");
            clock.Advance(TimeSpan.FromMilliseconds(250));
            using (var lease = await first)
            {
                lease.IsAcquired.Should().BeTrue("the per-pod limit decides while connecting");
            }

            for (var i = 0; i < 3; i++)
            {
                var next = limiter.AcquireAsync(1, TestContext.Current.CancellationToken);
                next.IsCompleted.Should().BeTrue("one request has already waited out the connect");
                (await next).Dispose();
            }

            context.Circuit.IsOpen.Should().BeFalse("starting is not an outage");
        }
        finally
        {
            release.Set();
        }
    }

    /// <summary>
    /// The Redis round trip has its own histogram, in seconds: ASP.NET Core's
    /// <c>time_in_queue</c> would otherwise be the only latency signal, and it mislabels it. Calls
    /// Redis answered carry no <c>error.type</c>; one that did not, the cause.
    /// </summary>
    [Fact]
    public async Task StoreDuration_IsRecordedPerRedisCall()
    {
        var config = RedisTestHost.FixedWindow(RedisTestHost.UniquePrefix(), permitLimit: 100);
        await using var app = await RedisTestHost.CreateAsync(redis.ConnectionString, config);
        await using var down = await RedisTestHost.CreateAsync(
            redis.ConnectionString,
            RedisTestHost.FixedWindow(RedisTestHost.UniquePrefix(), permitLimit: 100),
            configureServices: (services, configuration) =>
                services.AddApiPipelineRedisRateLimiting(configuration, _ => throw new RedisException("test: never connects")));

        var measurements = new List<(double Seconds, string? Policy, string? ErrorType)>();
        void Record(double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            string? policy = null, errorType = null;
            foreach (var tag in tags)
            {
                if (tag.Key == "apipipeline.rate_limiting.policy")
                {
                    policy = tag.Value as string;
                }
                else if (tag.Key == "error.type")
                {
                    errorType = tag.Value as string;
                }
            }

            lock (measurements)
            {
                measurements.Add((value, policy, errorType));
            }
        }

        string? unit = null;
        using var listener = Listen<double>(app.Services, StoreDurationInstrument, Record, instrument => unit = instrument.Unit);
        using var downListener = Listen<double>(down.Services, StoreDurationInstrument, Record);

        for (var i = 0; i < 3; i++)
        {
            (await app.GetStatusAsync()).Should().Be(HttpStatusCode.OK);
        }

        (await down.GetStatusAsync()).Should().Be(HttpStatusCode.OK, "the failure falls back per pod");

        unit.Should().Be("s");
        lock (measurements)
        {
            measurements.Should().HaveCount(4);
            measurements.Take(3).Should().AllSatisfy(m =>
            {
                m.Seconds.Should().BeGreaterThan(0);
                m.Policy.Should().Be("strict");
                m.ErrorType.Should().BeNull("Redis answered");
            });
            measurements[3].ErrorType.Should().Be(typeof(RedisException).FullName);
        }
    }

    /// <summary>
    /// The fallback counter says which policy lost sharing; the tag is the configured policy name.
    /// </summary>
    [Fact]
    public async Task FallbackCounter_IsTaggedWithPolicy()
    {
        var config = RedisTestHost.FixedWindow(RedisTestHost.UniquePrefix(), permitLimit: 10);
        await using var app = await RedisTestHost.CreateAsync("127.0.0.1:1,connectTimeout=200", config);

        var policies = new List<object?>();
        using var listener = Listen<long>(app, FallbackInstrument, (value, tags) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == "apipipeline.rate_limiting.policy")
                {
                    lock (policies)
                    {
                        policies.Add(tag.Value);
                    }
                }
            }
        });

        await app.GetStatusAsync();
        await app.GetStatusAsync();

        lock (policies)
        {
            policies.Should().HaveCount(2).And.OnlyContain(p => (string?)p == "strict");
        }
    }

    /// <summary>
    /// The fallback counter also says why Redis did not decide: the call timed out, the call failed
    /// otherwise, or the circuit was open. An alert can then tell a slow Redis from a broken one.
    /// </summary>
    [Fact]
    public async Task FallbackCounter_IsTaggedWithReason()
    {
        var (provider, context) = RedisTestHost.CreateLimiterContext(_ => redis.Multiplexer, failureThreshold: 2);
        await using var _ = provider;
        var reasons = new List<string?>();
        using var listener = Listen<long>(provider, FallbackInstrument, (_, tags) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == "apipipeline.rate_limiting.store.fallback_reason")
                {
                    lock (reasons)
                    {
                        reasons.Add(tag.Value as string);
                    }
                }
            }
        });
        using var timingOut = new ThrowingLimiter(context, () => new TimeoutException("test"));
        using var failing = new ThrowingLimiter(context, () => new InvalidOperationException("test"));

        (await timingOut.AcquireAsync(1, TestContext.Current.CancellationToken)).Dispose();
        (await failing.AcquireAsync(1, TestContext.Current.CancellationToken)).Dispose();
        (await failing.AcquireAsync(1, TestContext.Current.CancellationToken)).Dispose();

        lock (reasons)
        {
            reasons.Should().Equal("timeout", "error", "circuit_open");
        }
    }

    /// <summary>
    /// The degraded gauge is a 0/1 state, so its unit is the annotation <c>{state}</c>. Not
    /// <c>1</c>: the OpenTelemetry Prometheus mapping adds <c>_ratio</c> to a gauge with unit
    /// <c>1</c>, and the documented alert on <c>apipipeline_rate_limiting_store_degraded</c> would
    /// match no series. An annotation in braces adds no suffix.
    /// </summary>
    [Fact]
    public async Task DegradedGauge_HasAnAnnotationUnit_SoPrometheusAddsNoSuffix()
    {
        var (provider, _) = RedisTestHost.CreateLimiterContext(_ => redis.Multiplexer);
        await using var __ = provider;
        string? unit = "unset";

        using var listener = Listen<int>(provider, DegradedInstrument, (_, _) => { }, instrument => unit = instrument.Unit);

        unit.Should().Be("{state}");
    }

    /// <summary>
    /// The connection string can hold the Redis password; neither a log message nor a logged
    /// exception may carry it.
    /// </summary>
    [Fact]
    public async Task Password_NeverAppearsInLogs()
    {
        var logs = new CapturingLoggerProvider();
        var config = RedisTestHost.FixedWindow(RedisTestHost.UniquePrefix(), permitLimit: 10);
        await using var app = await RedisTestHost.CreateAsync(
            "127.0.0.1:1,password=s3cr3t-pw,connectTimeout=200", config, logs: logs);

        await app.GetStatusAsync();

        var messages = Snapshot(logs);
        messages.Should().Contain(m => m.Contains(UnavailableMessage, StringComparison.Ordinal), "the outage is logged");
        messages.Should().NotContain(m => m.Contains("s3cr3t-pw", StringComparison.Ordinal));
    }

    /// <summary>
    /// As above, but with a command timeout longer than StackExchange.Redis' own, so the logged
    /// exception is the client's (which names the endpoint) rather than the command timeout.
    /// </summary>
    [Fact]
    public async Task Password_NeverAppearsInLogs_WhenTheClientReportsTheError()
    {
        var logs = new CapturingLoggerProvider();
        var config = RedisTestHost.FixedWindow(RedisTestHost.UniquePrefix(), permitLimit: 10);
        config["ApiPipeline:RateLimiting:Redis:CommandTimeout"] = "00:00:10";
        await using var app = await RedisTestHost.CreateAsync(
            "127.0.0.1:1,password=s3cr3t-pw,connectTimeout=200,asyncTimeout=1000", config, logs: logs);

        await app.GetStatusAsync();

        var messages = Snapshot(logs);
        messages.Should().Contain(
            m => m.Contains(UnavailableMessage, StringComparison.Ordinal) && m.Contains(nameof(RedisConnectionException), StringComparison.Ordinal),
            "the client's own exception is logged");
        messages.Should().NotContain(m => m.Contains("s3cr3t-pw", StringComparison.Ordinal));
    }

    /// <summary>
    /// Redis down at startup is an outage like any other: the API starts and serves per-pod limits.
    /// </summary>
    [Fact]
    public async Task RedisDownAtStartup_DoesNotFailStart()
    {
        var config = RedisTestHost.FixedWindow(RedisTestHost.UniquePrefix(), permitLimit: 10);
        await using var app = await RedisTestHost.CreateAsync("127.0.0.1:1,connectTimeout=200", config);

        (await app.GetStatusAsync()).Should().Be(HttpStatusCode.OK);
    }

    /// <summary>
    /// A caller that gave up is not a Redis outage: the cancellation propagates, and neither opens
    /// the circuit nor logs the outage warning.
    /// </summary>
    [Fact]
    public async Task CancelledAcquire_DoesNotOpenTheCircuit()
    {
        var logs = new CapturingLoggerProvider();
        var (provider, limiter) = RedisTestHost.CreateLimiter(_ => redis.Multiplexer, logs);
        await using var _ = provider;
        using var __ = limiter;
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var acquire = async () => await limiter.AcquireAsync(1, cancelled.Token);

        await acquire.Should().ThrowAsync<OperationCanceledException>();
        provider.GetRequiredService<RedisCircuit>().IsOpen.Should().BeFalse();
        Snapshot(logs).Should().NotContain(m => m.Contains(UnavailableMessage, StringComparison.Ordinal));
    }

    /// <summary>
    /// The same while Redis is still working on the call: a caller cancelled mid-wait is not a
    /// timeout, even though the Redis call never finished.
    /// </summary>
    [Fact]
    public async Task CancelledWhileWaitingOnRedis_DoesNotOpenTheCircuit()
    {
        using var never = new ManualResetEventSlim();
        var (provider, limiter) = RedisTestHost.CreateLimiter(_ =>
        {
            never.Wait();
            throw new InvalidOperationException("test: never connects");
        });
        try
        {
            await using var _ = provider;
            using var __ = limiter;
            using var cancelled = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

            var acquire = async () => await limiter.AcquireAsync(1, cancelled.Token);

            await acquire.Should().ThrowAsync<OperationCanceledException>();
            provider.GetRequiredService<RedisCircuit>().IsOpen.Should().BeFalse();
        }
        finally
        {
            never.Set();
        }
    }

    /// <summary>
    /// A server error (here WRONGTYPE: the key holds a hash, not a counter) is Redis failing, not
    /// the application: the request is decided per pod rather than failing with a 500. One bad key
    /// stays that key's problem: with the default threshold the circuit stays closed, so every other
    /// partition keeps its shared limit.
    /// </summary>
    [Fact]
    public async Task ServerError_FallsBackPerPod_WithoutOpeningTheCircuit()
    {
        var prefix = RedisTestHost.UniquePrefix();
        var (provider, limiter) = RedisTestHost.CreateLimiter(
            _ => redis.Multiplexer, keyPrefix: prefix, failureThreshold: new ApiPipelineRedisRateLimitingOptions().FailureThreshold);
        await using var _ = provider;
        using var __ = limiter;
        await redis.Multiplexer.GetDatabase().HashSetAsync(
            RedisKeys.For(prefix, RedisTestHost.LimiterPartition), "field", "value");

        using var lease = await limiter.AcquireAsync(1, TestContext.Current.CancellationToken);

        lease.IsAcquired.Should().BeTrue("the per-pod limiter decides");
        provider.GetRequiredService<RedisCircuit>().IsOpen.Should().BeFalse("one failure is below the threshold");
    }

    /// <summary>
    /// A hot broken key, every call to it failing with WRONGTYPE from the real script, is Redis
    /// answering: it stays out of the failure rate, so the circuit stays closed for every other
    /// partition however many calls the key takes. The run of failures is set out of reach, so only
    /// the failure rate could open it.
    /// </summary>
    [Fact]
    public async Task HotBrokenKey_DoesNotOpenTheCircuitByFailureRate()
    {
        var prefix = RedisTestHost.UniquePrefix();
        var (provider, limiter) = RedisTestHost.CreateLimiter(_ => redis.Multiplexer, keyPrefix: prefix, failureThreshold: 1000);
        await using var _ = provider;
        using var __ = limiter;
        await redis.Multiplexer.GetDatabase().HashSetAsync(
            RedisKeys.For(prefix, RedisTestHost.LimiterPartition), "field", "value");

        for (var i = 0; i < 25; i++)
        {
            (await limiter.AcquireAsync(1, TestContext.Current.CancellationToken)).Dispose();
        }

        provider.GetRequiredService<RedisCircuit>().IsOpen.Should().BeFalse(
            "25 WRONGTYPE replies (over the minimum throughput of 20) are one broken key, not a brownout");
    }

    /// <summary>
    /// An exception outside StackExchange.Redis's <see cref="RedisException"/> hierarchy (here
    /// <see cref="RedisCommandException"/>, thrown when a command is disabled in the command map,
    /// and an arbitrary <see cref="InvalidOperationException"/>) must fall back per pod, not
    /// surface as a 500 on every request.
    /// </summary>
    [Theory]
    [InlineData(typeof(RedisCommandException))]
    [InlineData(typeof(InvalidOperationException))]
    public async Task UnexpectedExceptionType_FallsBackPerPod(Type exceptionType)
    {
        var (provider, context) = RedisTestHost.CreateLimiterContext(_ => redis.Multiplexer);
        await using var _ = provider;
        using var limiter = new ThrowingLimiter(context, () => (Exception)Activator.CreateInstance(exceptionType, "test")!);

        using var lease = await limiter.AcquireAsync(1, TestContext.Current.CancellationToken);

        lease.IsAcquired.Should().BeTrue("the per-pod limiter decides");
        provider.GetRequiredService<RedisCircuit>().IsOpen.Should().BeTrue("the failure was reported");
    }

    /// <summary>The caller's own cancellation is not a Redis failure: it propagates.</summary>
    [Fact]
    public async Task CallerCancellation_Propagates_AndLeavesCircuitClosed()
    {
        var (provider, context) = RedisTestHost.CreateLimiterContext(_ => redis.Multiplexer);
        await using var _ = provider;
        using var cancellation = new CancellationTokenSource();
        using var limiter = new ThrowingLimiter(context, () =>
        {
            cancellation.Cancel();
            return new OperationCanceledException(cancellation.Token);
        });

        var act = async () => await limiter.AcquireAsync(1, cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        provider.GetRequiredService<RedisCircuit>().IsOpen.Should().BeFalse();
    }

    /// <summary>
    /// While the per-pod twin has spent permits it has not refilled, the limiter must not report
    /// itself idle: the partitioned limiter would retire it after about 10 s, and a client that
    /// waits (honouring Retry-After) would come back to a fresh, full twin during the outage.
    /// </summary>
    [Fact]
    public async Task TwinInUse_KeepsLimiterFromIdling()
    {
        var (provider, limiter) = RedisTestHost.CreateLimiter(_ => throw new RedisException("test: down"));
        await using var _ = provider;
        using var __ = limiter;

        using (var lease = await limiter.AcquireAsync(1, TestContext.Current.CancellationToken))
        {
            lease.IsAcquired.Should().BeTrue("the per-pod limiter decides, 1 of 5 spent");
        }

        limiter.IdleDuration.Should().BeNull("the twin's window has not refilled, so its state must be kept");
    }

    /// <summary>With Redis deciding (no twin built), the limiter idles normally: its state is in Redis.</summary>
    [Fact]
    public async Task HealthyRedis_LimiterIdles()
    {
        var (provider, limiter) = RedisTestHost.CreateLimiter(_ => redis.Multiplexer);
        await using var _ = provider;
        using var __ = limiter;

        using (var lease = await limiter.AcquireAsync(1, TestContext.Current.CancellationToken))
        {
            lease.IsAcquired.Should().BeTrue();
        }

        limiter.IdleDuration.Should().NotBeNull();
    }

    /// <summary>
    /// A script result of the wrong shape is a Redis failure too, not an InvalidCastException.
    /// </summary>
    [Fact]
    public async Task MalformedResult_FallsBackPerPod()
    {
        var (provider, context) = RedisTestHost.CreateLimiterContext(_ => redis.Multiplexer);
        await using var _ = provider;
        using var limiter = new MalformedResultLimiter(context);

        using var lease = await limiter.AcquireAsync(1, TestContext.Current.CancellationToken);

        lease.IsAcquired.Should().BeTrue("the per-pod limiter decides");
        provider.GetRequiredService<RedisCircuit>().IsOpen.Should().BeTrue();
    }

    private static List<string> Snapshot(CapturingLoggerProvider logs)
    {
        lock (logs.Messages)
        {
            return [.. logs.Messages];
        }
    }

    /// <summary>
    /// Reads the degraded gauge of <paramref name="app"/>'s own meter (other hosts in the process
    /// have their own), or <c>null</c> when the gauge does not exist.
    /// </summary>
    private static int? ReadDegraded(WebApplication app)
    {
        int? value = null;
        using var listener = Listen<int>(app, DegradedInstrument, (measurement, _) => value = measurement);
        listener.RecordObservableInstruments();
        return value;
    }

    private static MeterListener Listen<T>(
        WebApplication app,
        string instrumentName,
        Action<T, ReadOnlySpan<KeyValuePair<string, object?>>> onMeasurement)
        where T : struct =>
        Listen(app.Services, instrumentName, onMeasurement);

    private static MeterListener Listen<T>(
        IServiceProvider services,
        string instrumentName,
        Action<T, ReadOnlySpan<KeyValuePair<string, object?>>> onMeasurement,
        Action<Instrument>? onPublished = null)
        where T : struct
    {
        var meterFactory = services.GetRequiredService<IMeterFactory>();
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == ApiPipelineTelemetry.MeterName
                    && ReferenceEquals(instrument.Meter.Scope, meterFactory)
                    && instrument.Name == instrumentName)
                {
                    onPublished?.Invoke(instrument);
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<T>((_, measurement, tags, _) => onMeasurement(measurement, tags));
        listener.Start();
        return listener;
    }

    /// <summary>A limiter whose "script" returns a plain string instead of the two-integer array.</summary>
    private sealed class ThrowingLimiter(RedisRateLimiterContext context, Func<Exception> exception)
        : RedisRateLimiter(RedisTestHost.LimiterPartition, RedisTestHost.LimiterPolicy, context)
    {
        protected override Task<RedisResult> ExecuteAsync(IDatabase db, RedisKey key, RedisValue leaseId, int permitCount, CancellationToken cancellationToken) =>
            Task.FromException<RedisResult>(exception());
    }

    private sealed class MalformedResultLimiter(RedisRateLimiterContext context)
        : RedisRateLimiter(RedisTestHost.LimiterPartition, RedisTestHost.LimiterPolicy, context)
    {
        protected override Task<RedisResult> ExecuteAsync(IDatabase db, RedisKey key, RedisValue leaseId, int permitCount, CancellationToken cancellationToken) =>
            Task.FromResult(RedisResult.Create((RedisValue)"not an array"));
    }
}
