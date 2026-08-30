using System.Threading.RateLimiting;
using FluentAssertions;

namespace ApiPipeline.NET.RateLimiting.Redis.Tests;

/// <summary>
/// Every rate-limit key expires (the README promises it): a partition nobody uses any more must not
/// stay in Redis. And a key must not expire before what it holds: a concurrency key that vanished
/// while a slot in it was still held would admit more requests than the limit.
/// </summary>
public sealed class KeyExpiryTests(RedisFixture redis)
{
    /// <summary>Each kind's key has a TTL once a permit is spent, no longer than the kind needs.</summary>
    [Theory]
    [InlineData(RateLimiterKind.FixedWindow, 60_000)]
    [InlineData(RateLimiterKind.SlidingWindow, 70_000)]
    [InlineData(RateLimiterKind.TokenBucket, 60_000)]
    [InlineData(RateLimiterKind.Concurrency, 30_000)]
    public async Task EveryKind_KeyExpires(RateLimiterKind kind, long maxTtlMs)
    {
        var (provider, context) = RedisTestHost.CreateLimiterContext(
            _ => redis.Multiplexer, concurrencyLeaseTimeout: TimeSpan.FromSeconds(30));
        await using var _ = provider;
        var policy = new ApiPipelineRateLimitPolicy
        {
            Name = "strict",
            Kind = kind,
            PermitLimit = 5,
            Window = TimeSpan.FromMinutes(1),
            SegmentsPerWindow = 6,
            TokensPerPeriod = 1,
            QueueLimit = 0,
        };
        using RateLimiter limiter = kind switch
        {
            RateLimiterKind.FixedWindow => new RedisFixedWindowRateLimiter(RedisTestHost.LimiterPartition, policy, context),
            RateLimiterKind.SlidingWindow => new RedisSlidingWindowRateLimiter(RedisTestHost.LimiterPartition, policy, context),
            // Five tokens at one per minute refill in five periods, plus one: six minutes.
            RateLimiterKind.TokenBucket => new RedisTokenBucketRateLimiter(
                RedisTestHost.LimiterPartition, new ApiPipelineRateLimitPolicy
                {
                    Name = "strict",
                    Kind = kind,
                    PermitLimit = 5,
                    Window = TimeSpan.FromSeconds(10),
                    TokensPerPeriod = 1,
                }, context),
            _ => new RedisConcurrencyLimiter(RedisTestHost.LimiterPartition, policy, context),
        };

        using var lease = await limiter.AcquireAsync(1, TestContext.Current.CancellationToken);
        lease.IsAcquired.Should().BeTrue();

        var ttl = await redis.Multiplexer.GetDatabase()
            .ExecuteAsync("PTTL", RedisKeys.For(context.KeyPrefix, RedisTestHost.LimiterPartition).ToString());

        ((long)ttl).Should().BeInRange(1, maxTtlMs, "the key expires, and not later than the kind needs");
    }

    /// <summary>
    /// A fixed-window key that lost its TTL (<c>PERSIST</c>, a restore, a failover) gets one again on
    /// the next call, admitted or rejected, instead of counting forever.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    public async Task FixedWindowKey_WithoutATtl_GetsOneAgain(int permitLimit)
    {
        var ct = TestContext.Current.CancellationToken;
        var (provider, context) = RedisTestHost.CreateLimiterContext(_ => redis.Multiplexer);
        await using var _ = provider;
        var policy = new ApiPipelineRateLimitPolicy { Name = "strict", PermitLimit = permitLimit, Window = TimeSpan.FromMinutes(1) };
        using var limiter = new RedisFixedWindowRateLimiter(RedisTestHost.LimiterPartition, policy, context);
        var key = RedisKeys.For(context.KeyPrefix, RedisTestHost.LimiterPartition, partitionKeySecret: null);
        var db = redis.Multiplexer.GetDatabase();

        (await limiter.AcquireAsync(1, ct)).IsAcquired.Should().BeTrue();
        (await db.KeyPersistAsync(key)).Should().BeTrue("the key had a TTL to remove");

        using var next = await limiter.AcquireAsync(1, ct);

        ((long)await db.ExecuteAsync("PTTL", key.ToString())).Should().BeInRange(1, 60_000);
        if (!next.IsAcquired)
        {
            next.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter).Should().BeTrue("a full window says when it resets");
            retryAfter.Should().BeGreaterThan(TimeSpan.Zero);
        }
    }

    /// <summary>
    /// Pods can disagree on <c>ConcurrencyLeaseTimeout</c> during a rollout. A pod with a shorter
    /// timeout must not shorten the key below a slot another pod holds for longer, or the key (and
    /// that slot with it) is deleted early and one request too many is admitted.
    /// </summary>
    [Fact]
    public async Task ConcurrencyKey_OutlivesItsLongestSlot()
    {
        var ct = TestContext.Current.CancellationToken;
        var prefix = RedisTestHost.UniquePrefix();
        var (longProvider, longPod) = RedisTestHost.CreateLimiterContext(
            _ => redis.Multiplexer, keyPrefix: prefix, concurrencyLeaseTimeout: TimeSpan.FromSeconds(120));
        await using var _ = longProvider;
        var (shortProvider, shortPod) = RedisTestHost.CreateLimiterContext(
            _ => redis.Multiplexer, keyPrefix: prefix, concurrencyLeaseTimeout: TimeSpan.FromSeconds(30));
        await using var __ = shortProvider;
        var policy = new ApiPipelineRateLimitPolicy { Name = "strict", Kind = RateLimiterKind.Concurrency, PermitLimit = 2, QueueLimit = 0 };
        using var longLimiter = new RedisConcurrencyLimiter(RedisTestHost.LimiterPartition, policy, longPod);
        using var shortLimiter = new RedisConcurrencyLimiter(RedisTestHost.LimiterPartition, policy, shortPod);

        using var longLease = await longLimiter.AcquireAsync(1, ct);
        using var shortLease = await shortLimiter.AcquireAsync(1, ct);
        longLease.IsAcquired.Should().BeTrue();
        shortLease.IsAcquired.Should().BeTrue();

        var ttl = (long)await redis.Multiplexer.GetDatabase()
            .ExecuteAsync("PTTL", RedisKeys.For(prefix, RedisTestHost.LimiterPartition).ToString());

        ttl.Should().BeGreaterThan(110_000, "the 120 s slot is still held");
    }
}
