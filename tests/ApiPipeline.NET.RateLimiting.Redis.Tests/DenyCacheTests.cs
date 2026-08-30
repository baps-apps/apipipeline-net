using System.Threading.RateLimiting;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;

namespace ApiPipeline.NET.RateLimiting.Redis.Tests;

/// <summary>
/// Once Redis has rejected a partition until a known time, the pod answers that partition's further
/// requests itself until then. A flood against one partition then costs Redis one call per pod per
/// window instead of one per request, so it cannot push Redis into the slowness that opens the
/// circuit and loosens every caller's limit to per pod.
/// </summary>
public sealed class DenyCacheTests(RedisFixture redis)
{
    private static ApiPipelineRateLimitPolicy Policy(int permitLimit) =>
        new() { Name = "strict", PermitLimit = permitLimit, Window = TimeSpan.FromSeconds(60) };

    /// <summary>
    /// After a rejection the Redis state is wiped, so Redis itself would admit the next request: a
    /// rejection anyway proves it was answered locally. Once the retry-after has passed, Redis
    /// decides again.
    /// </summary>
    [Fact]
    public async Task Rejection_IsAnsweredLocally_UntilRetryAfterPasses()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new FakeTimeProvider();
        var (provider, context) = RedisTestHost.CreateLimiterContext(_ => redis.Multiplexer, timeProvider: clock);
        await using var _ = provider;
        using var limiter = new RedisFixedWindowRateLimiter(RedisTestHost.LimiterPartition, Policy(1), context);

        using (var first = await limiter.AcquireAsync(1, ct))
        {
            first.IsAcquired.Should().BeTrue();
        }

        using var rejected = await limiter.AcquireAsync(1, ct);
        rejected.IsAcquired.Should().BeFalse();
        rejected.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter).Should().BeTrue();

        await WipeAsync(context);

        using (var cached = await limiter.AcquireAsync(1, ct))
        {
            cached.IsAcquired.Should().BeFalse("Redis already said no until the retry-after, so the pod answers");
            cached.TryGetMetadata(MetadataName.RetryAfter, out var remaining).Should().BeTrue();
            remaining.Should().BePositive().And.BeLessThanOrEqualTo(retryAfter);
        }

        clock.Advance(retryAfter + TimeSpan.FromMilliseconds(1));

        using var afterwards = await limiter.AcquireAsync(1, ct);
        afterwards.IsAcquired.Should().BeTrue("the retry-after has passed, so Redis decides again");
    }

    /// <summary>
    /// A rejection for n permits says nothing about fewer: with 1 of 3 left, a request for 2 is
    /// rejected but one for 1 must still reach Redis and be admitted.
    /// </summary>
    [Fact]
    public async Task SmallerRequest_AfterRejection_StillAsksRedis()
    {
        var ct = TestContext.Current.CancellationToken;
        var (provider, context) = RedisTestHost.CreateLimiterContext(
            _ => redis.Multiplexer, timeProvider: new FakeTimeProvider());
        await using var _ = provider;
        using var limiter = new RedisFixedWindowRateLimiter(RedisTestHost.LimiterPartition, Policy(3), context);

        using (var two = await limiter.AcquireAsync(2, ct))
        {
            two.IsAcquired.Should().BeTrue();
        }

        using (var twoMore = await limiter.AcquireAsync(2, ct))
        {
            twoMore.IsAcquired.Should().BeFalse();
        }

        using var one = await limiter.AcquireAsync(1, ct);
        one.IsAcquired.Should().BeTrue("one permit is left in Redis");
    }

    /// <summary>A probe (permit count 0) spends nothing and always asks Redis.</summary>
    [Fact]
    public async Task Probe_IsNotAnsweredFromTheCache()
    {
        var ct = TestContext.Current.CancellationToken;
        var (provider, context) = RedisTestHost.CreateLimiterContext(
            _ => redis.Multiplexer, timeProvider: new FakeTimeProvider());
        await using var _ = provider;
        using var limiter = new RedisFixedWindowRateLimiter(RedisTestHost.LimiterPartition, Policy(1), context);

        (await limiter.AcquireAsync(1, ct)).Dispose();
        (await limiter.AcquireAsync(1, ct)).IsAcquired.Should().BeFalse();
        await WipeAsync(context);

        using var probe = await limiter.AcquireAsync(0, ct);
        probe.IsAcquired.Should().BeTrue("the probe reached Redis, whose state is fresh");
    }

    private Task WipeAsync(RedisRateLimiterContext context) =>
        redis.Multiplexer.GetDatabase().KeyDeleteAsync(RedisKeys.For(context.KeyPrefix, RedisTestHost.LimiterPartition));
}
