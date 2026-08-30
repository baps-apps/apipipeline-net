using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.TestHost;

namespace ApiPipeline.NET.RateLimiting.Redis.Tests;

/// <summary>
/// The shared sliding window: one Redis hash of per-segment counters per partition, so the limit
/// holds across pods and permits return as segments slide out rather than all at once.
/// </summary>
public sealed class SlidingWindowTests(RedisFixture redis)
{
    /// <summary>
    /// Two replicas spend one bucket: 8 requests against a limit of 4, split across hosts, admit
    /// exactly 4. Per pod, all 8 would pass.
    /// </summary>
    [Fact]
    public async Task TwoHosts_ShareOneBucket()
    {
        var config = RedisTestHost.SlidingWindow(RedisTestHost.UniquePrefix(), permitLimit: 4, windowSeconds: 60, segmentsPerWindow: 4);
        await using var hostA = await RedisTestHost.CreateAsync(redis.ConnectionString, config);
        await using var hostB = await RedisTestHost.CreateAsync(redis.ConnectionString, config);

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 4; i++)
        {
            statuses.Add(await hostA.GetStatusAsync());
        }

        for (var i = 0; i < 4; i++)
        {
            statuses.Add(await hostB.GetStatusAsync());
        }

        statuses.Count(s => s == HttpStatusCode.OK).Should().Be(4, "both hosts spend one shared bucket");
        statuses.Count(s => s == HttpStatusCode.TooManyRequests).Should().Be(4);
    }

    /// <summary>
    /// The window slides: once the segments holding the spent permits fall out of the window, the
    /// permits return. The window is a minute and its sliding is simulated by moving the spent
    /// segments one whole window back, so the result does not depend on how fast the machine is.
    /// </summary>
    [Fact]
    public async Task PermitsReturn_AsSegmentsSlideOut()
    {
        var ct = TestContext.Current.CancellationToken;
        var (provider, context) = RedisTestHost.CreateLimiterContext(_ => redis.Multiplexer);
        await using var _ = provider;
        var policy = new ApiPipelineRateLimitPolicy
        {
            Name = "strict",
            Kind = RateLimiterKind.SlidingWindow,
            PermitLimit = 2,
            SegmentsPerWindow = 2,
            Window = TimeSpan.FromMinutes(1),
        };
        using var limiter = new RedisSlidingWindowRateLimiter(RedisTestHost.LimiterPartition, policy, context);
        var key = RedisKeys.For(context.KeyPrefix, RedisTestHost.LimiterPartition);

        (await limiter.AcquireAsync(1, ct)).IsAcquired.Should().BeTrue();
        (await limiter.AcquireAsync(1, ct)).IsAcquired.Should().BeTrue();
        (await limiter.AcquireAsync(1, ct)).IsAcquired.Should().BeFalse("the window is full");

        // Each field is a segment index; moving every one back by the segment count puts it a whole
        // window in the past.
        var db = redis.Multiplexer.GetDatabase();
        foreach (var segment in await db.HashGetAllAsync(key))
        {
            await db.HashDeleteAsync(key, segment.Name);
            await db.HashSetAsync(key, (long)segment.Name - 2, segment.Value);
        }

        // Another pod: this one remembers its rejection until the reported retry-after.
        using var otherPod = new RedisSlidingWindowRateLimiter(RedisTestHost.LimiterPartition, policy, context);
        (await otherPod.AcquireAsync(1, ct)).IsAcquired.Should().BeTrue("the spent segments left the window");
    }

    /// <summary>
    /// A rejection's <c>Retry-After</c> is when the oldest segment leaves the window, so it is
    /// positive and never longer than the window.
    /// </summary>
    [Fact]
    public async Task Rejection_RetryAfter_IsAtMostTheWindow()
    {
        var config = RedisTestHost.SlidingWindow(RedisTestHost.UniquePrefix(), permitLimit: 1, windowSeconds: 60, segmentsPerWindow: 4);
        await using var app = await RedisTestHost.CreateAsync(redis.ConnectionString, config);
        using var client = app.GetTestClient();

        using var first = await client.GetAsync("/test", TestContext.Current.CancellationToken);
        using var second = await client.GetAsync("/test", TestContext.Current.CancellationToken);

        first.StatusCode.Should().Be(HttpStatusCode.OK);
        second.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        second.Headers.RetryAfter.Should().NotBeNull();
        second.Headers.RetryAfter!.Delta.Should().NotBeNull();
        second.Headers.RetryAfter.Delta!.Value.TotalSeconds.Should().BeInRange(1, 60);
    }
}
