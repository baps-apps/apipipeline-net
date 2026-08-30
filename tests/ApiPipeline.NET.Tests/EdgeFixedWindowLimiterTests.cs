using System.Threading.RateLimiting;
using ApiPipeline.NET.RateLimiting;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;

namespace ApiPipeline.NET.Tests;

/// <summary>
/// The edge limiter's per-address window. The partitioned limiter retires a partition once its
/// <see cref="RateLimiter.IdleDuration"/> passes 10 seconds, so that value decides whether a
/// caller's count survives: retired mid-window, the caller would get a fresh allowance.
/// </summary>
public sealed class EdgeFixedWindowLimiterTests
{
    private readonly FakeTimeProvider _time = new();

    private EdgeFixedWindowLimiter Limiter(int permitLimit = 2) => new(permitLimit, TimeSpan.FromMinutes(1), _time);

    [Fact]
    public void IdleDuration_IsNull_WhilePermitsAreSpentInTheWindow()
    {
        using var limiter = Limiter();
        limiter.AttemptAcquire().IsAcquired.Should().BeTrue();

        _time.Advance(TimeSpan.FromSeconds(30));

        limiter.IdleDuration.Should().BeNull("retiring the partition now would forget what the caller spent");
    }

    [Fact]
    public void IdleDuration_CountsFromTheEndOfTheWindow()
    {
        using var limiter = Limiter();
        limiter.AttemptAcquire();

        _time.Advance(TimeSpan.FromSeconds(75));

        limiter.IdleDuration.Should().Be(TimeSpan.FromSeconds(15));
    }

    [Fact]
    public void IdleDuration_WithNothingSpent_CountsFromTheStart()
    {
        using var limiter = Limiter();

        _time.Advance(TimeSpan.FromSeconds(12));

        limiter.IdleDuration.Should().Be(TimeSpan.FromSeconds(12));
    }

    /// <summary>Zero permits asks whether one is available and spends nothing, as the framework limiters do.</summary>
    [Fact]
    public void AttemptAcquire_Zero_SpendsNothing()
    {
        using var limiter = Limiter(permitLimit: 1);

        limiter.AttemptAcquire(0).IsAcquired.Should().BeTrue();
        limiter.AttemptAcquire(1).IsAcquired.Should().BeTrue();
        limiter.AttemptAcquire(0).IsAcquired.Should().BeFalse();
    }

    [Fact]
    public void AttemptAcquire_MoreThanTheLimit_Throws()
    {
        using var limiter = Limiter(permitLimit: 2);

        var act = () => limiter.AttemptAcquire(3);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
