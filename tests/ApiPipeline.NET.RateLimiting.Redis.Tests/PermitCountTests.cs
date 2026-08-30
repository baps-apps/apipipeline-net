using System.Threading.RateLimiting;
using FluentAssertions;

namespace ApiPipeline.NET.RateLimiting.Redis.Tests;

/// <summary>
/// The Redis limiters honour the <see cref="RateLimiter"/> contract for the permit count, as the
/// framework's own limiters do: <c>n</c> spends <c>n</c> permits only if all are available, <c>0</c>
/// asks whether one is and spends none, more than <c>PermitLimit</c> throws. And a synchronous
/// attempt, which cannot reach Redis, says so.
/// </summary>
public sealed class PermitCountTests(RedisFixture redis)
{
    private static ApiPipelineRateLimitPolicy Policy(RateLimiterKind kind) => new()
    {
        Name = "strict",
        Kind = kind,
        PermitLimit = 3,
        Window = TimeSpan.FromSeconds(600),
        SegmentsPerWindow = 2,
        TokensPerPeriod = 1,
        QueueLimit = 0,
    };

    private RateLimiter Create(RateLimiterKind kind, ServiceProviderAndContext host) => kind switch
    {
        RateLimiterKind.FixedWindow => new RedisFixedWindowRateLimiter("p", Policy(kind), host.Context),
        RateLimiterKind.SlidingWindow => new RedisSlidingWindowRateLimiter("p", Policy(kind), host.Context),
        RateLimiterKind.TokenBucket => new RedisTokenBucketRateLimiter("p", Policy(kind), host.Context),
        _ => new RedisConcurrencyLimiter("p", Policy(kind), host.Context),
    };

    private sealed record ServiceProviderAndContext(ServiceProvider Provider, RedisRateLimiterContext Context) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Provider.DisposeAsync();
    }

    private ServiceProviderAndContext Host()
    {
        var (provider, context) = RedisTestHost.CreateLimiterContext(_ => redis.Multiplexer);
        return new ServiceProviderAndContext(provider, context);
    }

    /// <summary>
    /// A probe (<c>0</c>) spends nothing: after it, all three permits are still there; once they are
    /// spent, the probe reports none left.
    /// </summary>
    [Theory]
    [InlineData(RateLimiterKind.FixedWindow)]
    [InlineData(RateLimiterKind.SlidingWindow)]
    [InlineData(RateLimiterKind.TokenBucket)]
    [InlineData(RateLimiterKind.Concurrency)]
    public async Task ZeroPermits_IsAProbe_ThatSpendsNothing(RateLimiterKind kind)
    {
        await using var host = Host();
        using var limiter = Create(kind, host);
        var ct = TestContext.Current.CancellationToken;

        using (var probe = await limiter.AcquireAsync(0, ct))
        {
            probe.IsAcquired.Should().BeTrue("all permits are available");
        }

        using var held = await limiter.AcquireAsync(3, ct);
        held.IsAcquired.Should().BeTrue("the probe spent nothing, so all three were still there");

        using var empty = await limiter.AcquireAsync(0, ct);
        empty.IsAcquired.Should().BeFalse("no permit is left");
    }

    /// <summary><c>n</c> permits are spent together, and only if all are available.</summary>
    [Theory]
    [InlineData(RateLimiterKind.FixedWindow)]
    [InlineData(RateLimiterKind.SlidingWindow)]
    [InlineData(RateLimiterKind.TokenBucket)]
    [InlineData(RateLimiterKind.Concurrency)]
    public async Task SeveralPermits_AreSpentTogether_OrNotAtAll(RateLimiterKind kind)
    {
        await using var host = Host();
        using var limiter = Create(kind, host);
        var ct = TestContext.Current.CancellationToken;

        using var two = await limiter.AcquireAsync(2, ct);
        two.IsAcquired.Should().BeTrue();

        using (var tooMany = await limiter.AcquireAsync(2, ct))
        {
            tooMany.IsAcquired.Should().BeFalse("only one permit is left");
        }

        using var last = await limiter.AcquireAsync(1, ct);
        last.IsAcquired.Should().BeTrue("the rejected request spent nothing");
    }

    /// <summary>A concurrency lease of several permits gives them all back when disposed.</summary>
    [Fact]
    public async Task Concurrency_MultiPermitLease_ReleasesAllItsPermits()
    {
        await using var host = Host();
        using var limiter = Create(RateLimiterKind.Concurrency, host);
        var ct = TestContext.Current.CancellationToken;

        (await limiter.AcquireAsync(3, ct)).Dispose();

        // The release runs in the background; wait for it.
        for (var i = 0; i < 100; i++)
        {
            using var again = await limiter.AcquireAsync(3, ct);
            if (again.IsAcquired)
            {
                return;
            }

            await Task.Delay(20, ct);
        }

        throw new Xunit.Sdk.XunitException("the three permits were not released");
    }

    /// <summary>More permits than the limit can never be granted: rejected as the framework does.</summary>
    [Fact]
    public async Task MorePermitsThanTheLimit_Throws()
    {
        await using var host = Host();
        using var limiter = Create(RateLimiterKind.FixedWindow, host);

        var act = async () => await limiter.AcquireAsync(4, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
        limiter.Invoking(l => l.AttemptAcquire(4)).Should().Throw<ArgumentOutOfRangeException>();
    }

    /// <summary>
    /// A synchronous attempt cannot reach Redis without blocking: it is not acquired, and the lease
    /// says to call <c>AcquireAsync</c>, so a caller outside the middleware is not left guessing.
    /// </summary>
    [Fact]
    public async Task AttemptAcquire_IsNotAcquired_AndSaysWhy()
    {
        await using var host = Host();
        using var limiter = Create(RateLimiterKind.FixedWindow, host);

        using var lease = limiter.AttemptAcquire(1);

        lease.IsAcquired.Should().BeFalse();
        lease.TryGetMetadata(MetadataName.ReasonPhrase, out var reason).Should().BeTrue();
        reason.Should().Contain("AcquireAsync");
    }
}
