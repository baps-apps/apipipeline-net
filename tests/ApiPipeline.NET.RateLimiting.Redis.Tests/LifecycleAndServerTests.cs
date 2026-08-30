using System.Diagnostics.Metrics;
using ApiPipeline.NET.Observability;
using FluentAssertions;
using StackExchange.Redis;

namespace ApiPipeline.NET.RateLimiting.Redis.Tests;

/// <summary>
/// A disposed limiter refuses work as the framework's own limiters do; the add-on's instruments live
/// on the core package's meter, not a second one of the same name; and the server a pod shares
/// limits through is checked once for what would quietly break them: a version older than the
/// scripts need, or an eviction policy that can delete their keys.
/// </summary>
public sealed class LifecycleAndServerTests(RedisFixture redis)
{
    private const string VersionWarning = "shared rate limiting needs";
    private const string EvictionWarning = "can evict rate limit keys";

    /// <summary>After dispose, both ways of acquiring throw, as <c>FixedWindowRateLimiter</c> does.</summary>
    [Fact]
    public async Task DisposedLimiter_Throws()
    {
        var (provider, limiter) = RedisTestHost.CreateLimiter(_ => redis.Multiplexer);
        await using var _ = provider;
        limiter.Dispose();

        var attempt = () => limiter.AttemptAcquire(1);
        attempt.Should().Throw<ObjectDisposedException>();
        var acquire = async () => await limiter.AcquireAsync(1, TestContext.Current.CancellationToken);
        await acquire.Should().ThrowAsync<ObjectDisposedException>();
    }

    /// <summary>
    /// One meter per host for <c>ApiPipeline.NET</c>: the add-on's instruments are on the same
    /// <see cref="Meter"/> instance as the core package's, so they share its version.
    /// </summary>
    [Fact]
    public async Task Instruments_ShareTheCoreMeter()
    {
        var config = RedisTestHost.FixedWindow(RedisTestHost.UniquePrefix(), permitLimit: 10);
        await using var app = await RedisTestHost.CreateAsync(redis.ConnectionString, config);
        var meterFactory = app.Services.GetRequiredService<IMeterFactory>();
        var meters = new Dictionary<string, Meter>();

        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, _) =>
            {
                if (instrument.Meter.Name == ApiPipelineTelemetry.MeterName && ReferenceEquals(instrument.Meter.Scope, meterFactory))
                {
                    lock (meters)
                    {
                        meters[instrument.Name] = instrument.Meter;
                    }
                }
            },
        };
        listener.Start();

        meters.Should().ContainKey("apipipeline.rate_limiting.rejected").And.ContainKey("apipipeline.rate_limiting.store.fallback");
        meters["apipipeline.rate_limiting.store.fallback"].Should().BeSameAs(meters["apipipeline.rate_limiting.rejected"]);
    }

    /// <summary>The rules, apart from any server.</summary>
    [Theory]
    [InlineData("4.0.14", "noeviction", 0L, true, false)]
    [InlineData("5.0.0", "noeviction", 0L, false, false)]
    [InlineData("7.4.1", "allkeys-lru", 1_073_741_824L, false, true)]
    [InlineData("7.4.1", "volatile-ttl", 1_073_741_824L, false, true)]
    [InlineData("7.4.1", "allkeys-lru", 0L, false, false)]
    [InlineData("7.4.1", "noeviction", 1_073_741_824L, false, false)]
    public void Rules(string version, string policy, long maxmemory, bool tooOld, bool evicts)
    {
        RedisServerCheck.IsTooOld(Version.Parse(version)).Should().Be(tooOld);
        RedisServerCheck.CanEvict(policy, maxmemory).Should().Be(evicts);
    }

    /// <summary>
    /// A server that would evict the keys under memory pressure is reported once, by endpoint and
    /// policy. The setting is the test container's own, restored afterwards; its 1 GB ceiling is
    /// far above what the tests store, so nothing is evicted meanwhile.
    /// </summary>
    [Fact]
    public async Task EvictingServer_IsReported()
    {
        var logs = new CapturingLoggerProvider();
        await using var admin = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString + ",allowAdmin=true");
        var server = admin.GetServers()[0];
        var original = await server.ConfigGetAsync("maxmemory*");
        try
        {
            await server.ConfigSetAsync("maxmemory", "1gb");
            await server.ConfigSetAsync("maxmemory-policy", "allkeys-lru");

            using var factory = LoggerFactory.Create(l => l.AddProvider(logs));
            await new RedisServerCheck(factory.CreateLogger<RedisServerCheck>()).CheckAsync(admin);
        }
        finally
        {
            foreach (var (key, value) in original)
            {
                await server.ConfigSetAsync(key, value);
            }
        }

        lock (logs.Messages)
        {
            logs.Messages.Should().ContainSingle(m => m.Contains(EvictionWarning, StringComparison.Ordinal))
                .Which.Should().Contain("allkeys-lru");
            logs.Messages.Should().NotContain(m => m.Contains(VersionWarning, StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// The check runs by itself, once, after Redis first answers a limiter call: never at startup,
    /// which must not wait for Redis, and not on a server that has never answered.
    /// </summary>
    [Fact]
    public async Task Check_RunsOnce_AfterRedisFirstAnswers()
    {
        var logs = new CapturingLoggerProvider();
        var config = RedisTestHost.FixedWindow(RedisTestHost.UniquePrefix(), permitLimit: 10);
        await using var app = await RedisTestHost.CreateAsync(redis.ConnectionString, config, logs: logs);
        var check = app.Services.GetRequiredService<RedisServerCheck>();
        check.Running.Should().BeNull("nothing has asked Redis yet");

        await app.GetStatusAsync();
        var first = check.Running;
        first.Should().NotBeNull();
        await first!;
        await app.GetStatusAsync();

        check.Running.Should().BeSameAs(first, "the server is checked once per process");
        lock (logs.Messages)
        {
            logs.Messages.Should().NotContain(m => m.Contains(VersionWarning, StringComparison.Ordinal)
                || m.Contains(EvictionWarning, StringComparison.Ordinal), "the test server is 7.4 with noeviction");
        }
    }
}
