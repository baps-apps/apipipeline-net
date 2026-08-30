using System.Globalization;
using System.Net;
using FluentAssertions;
using StackExchange.Redis;

namespace ApiPipeline.NET.RateLimiting.Redis.Tests;

/// <summary>
/// How many Redis calls one request makes, counted by Redis itself (its script command stats). The
/// documentation sizes Redis and the added latency from these numbers: one call per policy that
/// applies to the request, made one after the other, so <c>GlobalPolicy</c> doubles both.
/// </summary>
public sealed class RedisCallCountTests(RedisFixture redis)
{
    private const int Requests = 10;

    /// <summary>Without <c>GlobalPolicy</c>: one call, for the endpoint's (or the default) policy.</summary>
    [Fact]
    public async Task NoGlobalPolicy_OneCallPerRequest()
    {
        var config = RedisTestHost.FixedWindow(RedisTestHost.UniquePrefix(), permitLimit: 1000);

        (await CallsPerRequestAsync(config, "/test")).Should().Be(1);
    }

    /// <summary>
    /// With <c>GlobalPolicy</c>, an endpoint without its own policy: the global policy, then the
    /// default policy.
    /// </summary>
    [Fact]
    public async Task GlobalPolicy_DefaultEndpoint_TwoCallsPerRequest()
    {
        var config = WithGlobalPolicy();

        (await CallsPerRequestAsync(config, "/test")).Should().Be(2);
    }

    /// <summary>
    /// With <c>GlobalPolicy</c>, an endpoint with its own policy: the global policy, then the
    /// endpoint's. The default policy is not also applied, so still two, not three.
    /// </summary>
    [Fact]
    public async Task GlobalPolicy_NamedEndpoint_TwoCallsPerRequest()
    {
        var config = WithGlobalPolicy();

        (await CallsPerRequestAsync(config, "/named")).Should().Be(2);
    }

    /// <summary>
    /// <c>GlobalPolicy</c> naming <c>DefaultPolicy</c> makes the default global: an endpoint without
    /// its own policy is limited by it once, not twice.
    /// </summary>
    [Fact]
    public async Task GlobalPolicyIsDefault_DefaultEndpoint_OneCallPerRequest()
    {
        var config = WithGlobalPolicy();
        config["ApiPipeline:RateLimiting:GlobalPolicy"] = "strict";

        (await CallsPerRequestAsync(config, "/test")).Should().Be(1);
    }

    private static Dictionary<string, string?> WithGlobalPolicy()
    {
        var config = RedisTestHost.FixedWindow(RedisTestHost.UniquePrefix(), permitLimit: 1000);
        config["ApiPipeline:RateLimiting:Policies:1:Name"] = "global";
        config["ApiPipeline:RateLimiting:Policies:1:Kind"] = "FixedWindow";
        config["ApiPipeline:RateLimiting:Policies:1:PermitLimit"] = "1000";
        config["ApiPipeline:RateLimiting:Policies:1:Window"] = "00:01:00";
        config["ApiPipeline:RateLimiting:Policies:2:Name"] = "named";
        config["ApiPipeline:RateLimiting:Policies:2:Kind"] = "FixedWindow";
        config["ApiPipeline:RateLimiting:Policies:2:PermitLimit"] = "1000";
        config["ApiPipeline:RateLimiting:Policies:2:Window"] = "00:01:00";
        config["ApiPipeline:RateLimiting:GlobalPolicy"] = "global";
        return config;
    }

    private async Task<double> CallsPerRequestAsync(Dictionary<string, string?> config, string path)
    {
        await using var app = await RedisTestHost.CreateAsync(
            redis.ConnectionString,
            config,
            configureApp: a => a.MapGet("/named", () => "ok").RequireRateLimiting("named"));

        // Warm up: connect and load the scripts, which may take an extra EVAL per script.
        (await app.GetStatusAsync(path: path)).Should().Be(HttpStatusCode.OK);

        var before = await ScriptCallsAsync();
        for (var i = 0; i < Requests; i++)
        {
            (await app.GetStatusAsync(path: path)).Should().Be(HttpStatusCode.OK);
        }

        var after = await ScriptCallsAsync();
        return (after - before) / (double)Requests;
    }

    // EVAL and EVALSHA calls the server has run so far (tests in this assembly run one at a time).
    // INFO is an admin command, so over a connection of its own.
    private async Task<long> ScriptCallsAsync()
    {
        await using var admin = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString + ",allowAdmin=true");
        var server = admin.GetServer(admin.GetEndPoints()[0]);
        var stats = await server.InfoAsync("commandstats");
        long calls = 0;
        foreach (var (key, value) in stats.SelectMany(g => g))
        {
            if (key is "cmdstat_eval" or "cmdstat_evalsha")
            {
                var field = value.Split(',').First(f => f.StartsWith("calls=", StringComparison.Ordinal));
                calls += long.Parse(field["calls=".Length..], CultureInfo.InvariantCulture);
            }
        }

        return calls;
    }
}
