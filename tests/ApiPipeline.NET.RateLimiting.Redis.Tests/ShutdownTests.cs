using FluentAssertions;
using StackExchange.Redis;

namespace ApiPipeline.NET.RateLimiting.Redis.Tests;

/// <summary>
/// A concurrency release is sent in the background, so the request does not wait on Redis. One sent
/// while the host shuts down must still reach Redis, or its slot stays taken on every pod until the
/// lease timeout passes.
/// </summary>
public sealed class ShutdownTests(RedisFixture redis)
{
    // About half a second on a laptop; a slower machine only makes the window wider.
    private const int BusyIterations = 300_000_000;

    // Keeps the server busy for a while (a counted loop: TIME is frozen inside a script), so
    // commands sent meanwhile queue behind it.
    private const string Busy = """
        local x = 0
        for i = 1, tonumber(ARGV[1]) do x = x + i end
        return 1
        """;

    /// <summary>
    /// A release still queued when the host stops and disposes the connection the package owns is
    /// sent, not dropped: disposing the multiplexer lets queued commands complete (it sends
    /// <c>QUIT</c> after them). The slot is free in Redis afterwards.
    /// </summary>
    [Fact]
    public async Task ReleaseInFlight_AtShutdown_ReachesRedis()
    {
        var ct = TestContext.Current.CancellationToken;
        var prefix = RedisTestHost.UniquePrefix();
        var config = RedisTestHost.Concurrency(prefix, permitLimit: 1);
        var app = await RedisTestHost.CreateAsync(redis.ConnectionString, config);
        var factory = app.Services.GetRequiredService<IApiPipelineRateLimiterFactory>();
        var policy = new ApiPipelineRateLimitPolicy { Name = "strict", Kind = RateLimiterKind.Concurrency, PermitLimit = 1, QueueLimit = 0 };
        var limiter = factory.Create("partition", policy);
        var lease = await limiter.AcquireAsync(1, ct);
        lease.IsAcquired.Should().BeTrue();

        await using var blocker = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        var busy = blocker.GetDatabase().ScriptEvaluateAsync(Busy, [], [BusyIterations]);
        await Task.Delay(100, ct);

        lease.Dispose();
        busy.IsCompleted.Should().BeFalse("the release must be queued behind the busy script while the host shuts down");
        await app.StopAsync(ct);
        await app.DisposeAsync();
        await busy;

        var held = await redis.Multiplexer.GetDatabase().SortedSetLengthAsync(RedisKeys.For(prefix, "partition"));
        held.Should().Be(0, "the release sent during shutdown reached Redis");
    }
}
