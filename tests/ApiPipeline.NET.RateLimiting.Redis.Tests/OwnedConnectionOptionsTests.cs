using FluentAssertions;

namespace ApiPipeline.NET.RateLimiting.Redis.Tests;

/// <summary>
/// A limiter stops waiting after <c>CommandTimeout</c>, but StackExchange.Redis keeps the
/// abandoned command until its own <c>AsyncTimeout</c> (5 s by default). In a brownout those
/// abandoned commands pile up on the connection, so the connection the package opens itself times
/// out with the limiter.
/// </summary>
public sealed class OwnedConnectionOptionsTests
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromMilliseconds(250);

    /// <summary>The client gives up on a command when the limiter does.</summary>
    [Fact]
    public void AsyncTimeout_DefaultsToTheCommandTimeout()
    {
        var options = RedisConnectionSource.CreateOwnedOptions("localhost:6379", CommandTimeout);

        options.AsyncTimeout.Should().Be(250);
    }

    /// <summary>A shorter timeout set in the connection string is kept.</summary>
    [Fact]
    public void AsyncTimeout_ShorterInTheConnectionString_IsKept()
    {
        var options = RedisConnectionSource.CreateOwnedOptions("localhost:6379,asyncTimeout=100", CommandTimeout);

        options.AsyncTimeout.Should().Be(100);
    }

    /// <summary>
    /// A longer one is capped: past the command timeout nobody waits for the answer, so keeping the
    /// command only adds to the backlog.
    /// </summary>
    [Fact]
    public void AsyncTimeout_LongerInTheConnectionString_IsCapped()
    {
        var options = RedisConnectionSource.CreateOwnedOptions("localhost:6379,asyncTimeout=5000", CommandTimeout);

        options.AsyncTimeout.Should().Be(250);
    }

    /// <summary>Redis being down at startup must not fail the connection attempt for good.</summary>
    [Fact]
    public void AbortOnConnectFail_IsOff()
    {
        var options = RedisConnectionSource.CreateOwnedOptions("localhost:6379", CommandTimeout);

        options.AbortOnConnectFail.Should().BeFalse();
    }
}
