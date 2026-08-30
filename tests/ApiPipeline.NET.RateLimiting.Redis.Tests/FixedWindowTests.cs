using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration.Memory;

namespace ApiPipeline.NET.RateLimiting.Redis.Tests;

/// <summary>
/// The shared fixed window: one Redis counter per partition, so the configured limit is the limit
/// for the whole API rather than for each pod.
/// </summary>
public sealed class FixedWindowTests(RedisFixture redis)
{
    /// <summary>
    /// The reason the package exists: two replicas spend one bucket. Per pod, 8 requests against a
    /// limit of 5 split 4 + 4 would all pass; shared, exactly 5 pass.
    /// </summary>
    [Fact]
    public async Task TwoHosts_ShareOneBucket()
    {
        var config = RedisTestHost.FixedWindow(RedisTestHost.UniquePrefix(), permitLimit: 5);
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

        statuses.Count(s => s == HttpStatusCode.OK).Should().Be(5, "both hosts spend one shared bucket");
        statuses.Count(s => s == HttpStatusCode.TooManyRequests).Should().Be(3);
    }

    /// <summary>
    /// A rejected caller is told when the window ends, from the key's remaining time in Redis, so
    /// <c>Retry-After</c> stays correct even though no pod holds the window.
    /// </summary>
    [Fact]
    public async Task Rejection_RetryAfter_IsWithinTheWindow()
    {
        var config = RedisTestHost.FixedWindow(RedisTestHost.UniquePrefix(), permitLimit: 1, windowSeconds: 60);
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

    /// <summary>
    /// Settings are read once at startup, with the shared store as without it: a limit raised
    /// after start is not applied until restart.
    /// </summary>
    [Fact]
    public async Task LimitRaisedAfterStartup_IsNotApplied()
    {
        var config = RedisTestHost.FixedWindow(RedisTestHost.UniquePrefix(), permitLimit: 1);
        await using var app = await RedisTestHost.CreateAsync(redis.ConnectionString, config);

        (await app.GetStatusAsync()).Should().Be(HttpStatusCode.OK);
        (await app.GetStatusAsync()).Should().Be(HttpStatusCode.TooManyRequests);

        var root = (IConfigurationRoot)app.Services.GetRequiredService<IConfiguration>();
        foreach (var provider in root.Providers.OfType<MemoryConfigurationProvider>())
        {
            provider.Set("ApiPipeline:RateLimiting:Policies:0:PermitLimit", "3");
        }

        root.Reload();

        (await app.GetStatusAsync()).Should().Be(HttpStatusCode.TooManyRequests, "the startup limit, 1, still applies");
    }
}
