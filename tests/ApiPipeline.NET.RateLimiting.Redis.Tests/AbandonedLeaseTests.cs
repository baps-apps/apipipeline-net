using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.RateLimiting;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;

namespace ApiPipeline.NET.RateLimiting.Redis.Tests;

/// <summary>
/// A concurrency lease that is never disposed (a caller bug) must not hold its slot across every pod
/// for the life of this one. The renewal timer keeps a held lease's slot alive, so it must not also
/// keep the lease itself alive: once nothing else references the lease, its slot is released at the
/// next renewal instead of renewed.
/// </summary>
public sealed class AbandonedLeaseTests(RedisFixture redis)
{
    private static readonly ApiPipelineRateLimitPolicy Policy = new()
    {
        Name = "strict",
        Kind = RateLimiterKind.Concurrency,
        PermitLimit = 1,
        QueueLimit = 0,
    };

    /// <summary>An abandoned lease is collected, and its slot freed at the next renewal.</summary>
    [Fact]
    public async Task AbandonedLease_IsCollected_AndItsSlotFreed()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new FakeTimeProvider();
        var (provider, context) = RedisTestHost.CreateLimiterContext(_ => redis.Multiplexer, timeProvider: clock);
        await using var _ = provider;
        using var limiter = new RedisConcurrencyLimiter(RedisTestHost.LimiterPartition, Policy, context);

        var abandoned = await AcquireAndAbandonAsync(limiter);
        using (var blocked = await limiter.AcquireAsync(1, ct))
        {
            blocked.IsAcquired.Should().BeFalse("the abandoned lease still holds the only slot");
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        abandoned.IsAlive.Should().BeFalse("only the renewal timer still referred to the lease");

        clock.Advance(context.ConcurrencyLeaseTimeout / 3);

        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(10))
        {
            using var next = await limiter.AcquireAsync(1, ct);
            if (next.IsAcquired)
            {
                return;
            }

            await Task.Delay(50, ct);
        }

        throw new Xunit.Sdk.XunitException("the abandoned lease's slot was not freed at the next renewal");
    }

    /// <summary>A lease still referenced keeps being renewed: only an unreachable one is released.</summary>
    [Fact]
    public async Task ReferencedLease_KeepsItsSlotAcrossRenewals()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new FakeTimeProvider();
        var (provider, context) = RedisTestHost.CreateLimiterContext(_ => redis.Multiplexer, timeProvider: clock);
        await using var _ = provider;
        using var limiter = new RedisConcurrencyLimiter(RedisTestHost.LimiterPartition, Policy, context);

        using var held = await limiter.AcquireAsync(1, ct);
        held.IsAcquired.Should().BeTrue();

        for (var i = 0; i < 3; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            clock.Advance(context.ConcurrencyLeaseTimeout / 3);
        }

        await Task.Delay(200, ct);
        using var second = await limiter.AcquireAsync(1, ct);
        second.IsAcquired.Should().BeFalse("the held lease keeps its slot");
        GC.KeepAlive(held);
    }

    /// <summary>
    /// The collection above happens only when the GC runs, which a quiet process may not do for
    /// hours. <c>ConcurrencyLeaseMaxDuration</c> bounds the wait whatever the GC does: a lease held
    /// that long gives its slot back at the next renewal.
    /// </summary>
    [Fact]
    public async Task LeaseHeldPastTheMaximumDuration_GivesItsSlotBack()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new FakeTimeProvider();
        var maximum = TimeSpan.FromMinutes(5);
        var (provider, context) = RedisTestHost.CreateLimiterContext(
            _ => redis.Multiplexer, timeProvider: clock, concurrencyLeaseMaxDuration: maximum);
        await using var _ = provider;
        using var limiter = new RedisConcurrencyLimiter(RedisTestHost.LimiterPartition, Policy, context);
        var renewal = context.ConcurrencyLeaseTimeout / 3;

        // Still referenced, so only the maximum duration can release it.
        using var held = await limiter.AcquireAsync(1, ct);
        held.IsAcquired.Should().BeTrue();

        while (clock.GetUtcNow() - clock.Start < maximum - renewal)
        {
            clock.Advance(renewal);
        }

        await Task.Delay(200, ct);
        using (var blocked = await limiter.AcquireAsync(1, ct))
        {
            blocked.IsAcquired.Should().BeFalse("the lease is still within its maximum duration");
        }

        clock.Advance(renewal);

        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(10))
        {
            using var next = await limiter.AcquireAsync(1, ct);
            if (next.IsAcquired)
            {
                GC.KeepAlive(held);
                return;
            }

            await Task.Delay(50, ct);
        }

        throw new Xunit.Sdk.XunitException("the lease kept its slot past ConcurrencyLeaseMaxDuration");
    }

    // Not inlined, so no local of the caller keeps the lease reachable.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference> AcquireAndAbandonAsync(RateLimiter limiter)
    {
        var lease = await limiter.AcquireAsync(1, TestContext.Current.CancellationToken);
        lease.IsAcquired.Should().BeTrue();
        return new WeakReference(lease);
    }
}
