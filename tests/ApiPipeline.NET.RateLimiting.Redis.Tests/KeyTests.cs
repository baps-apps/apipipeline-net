using System.Net;
using FluentAssertions;
using StackExchange.Redis;

namespace ApiPipeline.NET.RateLimiting.Redis.Tests;

/// <summary>
/// Redis key names: hashed so they hold no personal data, bounded in length, and isolated per
/// application so two APIs sharing a Redis never share a bucket.
/// </summary>
public sealed class KeyTests(RedisFixture redis)
{
    /// <summary>
    /// The partition key holds the user id. Key names are visible to anyone who can run
    /// <c>SCAN</c> or read a slow log, so the id must never appear in one.
    /// </summary>
    [Fact]
    public async Task Keys_ContainNoRawIdentity()
    {
        var prefix = RedisTestHost.UniquePrefix();
        await using var app = await RedisTestHost.CreateAsync(
            redis.ConnectionString,
            RedisTestHost.FixedWindow(prefix, permitLimit: 5));

        (await app.GetStatusAsync(user: "alice@example.com")).Should().Be(HttpStatusCode.OK);

        var keys = new List<string>();
        var server = redis.Multiplexer.GetServers()[0];
        await foreach (var key in server.KeysAsync(pattern: prefix + "*"))
        {
            keys.Add(key.ToString());
        }

        keys.Should().NotBeEmpty("the request must have written its counter");
        keys.Should().OnlyContain(k => !k.Contains("alice", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The default prefix is the application and environment name, so two APIs pointed at one
    /// Redis keep separate buckets without any configuration.
    /// </summary>
    [Fact]
    public async Task DifferentApplications_DoNotShareBuckets()
    {
        var config = RedisTestHost.FixedWindow(keyPrefix: null, permitLimit: 1);
        await using var apiA = await RedisTestHost.CreateAsync(redis.ConnectionString, config, applicationName: "api-a");
        await using var apiB = await RedisTestHost.CreateAsync(redis.ConnectionString, config, applicationName: "api-b");

        (await apiA.GetStatusAsync()).Should().Be(HttpStatusCode.OK);
        (await apiB.GetStatusAsync()).Should().Be(HttpStatusCode.OK, "api-b has its own bucket");
    }

    /// <summary>
    /// A caller controls its user id's length; the key must not grow with it, or a long id would
    /// cost Redis memory and bandwidth on every request.
    /// </summary>
    [Fact]
    public void LongUserId_ProducesFixedLengthKey()
    {
        var key = RedisKeys.For("p", new string('u', 2048));

        key.ToString().Length.Should().Be("p:apipipeline:rl:{}".Length + 32);
        key.ToString().Should().MatchRegex("^p:apipipeline:rl:\\{[0-9a-f]{32}\\}$");
    }
}
