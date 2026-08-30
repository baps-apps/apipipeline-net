using System.Net;
using FluentAssertions;
using StackExchange.Redis;

namespace ApiPipeline.NET.RateLimiting.Redis.Tests;

/// <summary>
/// The shared token bucket: one Redis hash (tokens, last replenish time) per partition, so the
/// burst allowance holds across pods and tokens return one whole period at a time.
/// </summary>
public sealed class TokenBucketTests(RedisFixture redis)
{
    private const string UnavailableMessage = "Shared rate limiting is unavailable";

    /// <summary>
    /// Two replicas spend one bucket: a capacity of 3 split across hosts admits exactly 3. Per pod,
    /// each host would admit 3.
    /// </summary>
    [Fact]
    public async Task TwoHosts_ShareOneBucket()
    {
        var config = RedisTestHost.TokenBucket(RedisTestHost.UniquePrefix(), permitLimit: 3, tokensPerPeriod: 1, windowSeconds: 60);
        await using var hostA = await RedisTestHost.CreateAsync(redis.ConnectionString, config);
        await using var hostB = await RedisTestHost.CreateAsync(redis.ConnectionString, config);

        var statuses = new List<HttpStatusCode>
        {
            await hostA.GetStatusAsync(),
            await hostB.GetStatusAsync(),
            await hostA.GetStatusAsync(),
            await hostB.GetStatusAsync(),
            await hostA.GetStatusAsync(),
        };

        statuses.Take(3).Should().OnlyContain(s => s == HttpStatusCode.OK, "the bucket holds 3 tokens");
        statuses.Skip(3).Should().OnlyContain(s => s == HttpStatusCode.TooManyRequests, "both hosts spend one bucket");
    }

    /// <summary>
    /// A drained bucket refills by <c>TokensPerPeriod</c> each whole period: once one period has
    /// passed exactly one token is back, then the bucket is empty again. The period is a minute and
    /// its passing is simulated by moving the bucket's replenish time back one period, so a slow
    /// machine can neither refill the bucket early nor miss the refill.
    /// </summary>
    [Fact]
    public async Task Tokens_Replenish_PerPeriod()
    {
        var ct = TestContext.Current.CancellationToken;
        var (provider, context) = RedisTestHost.CreateLimiterContext(_ => redis.Multiplexer);
        await using var _ = provider;
        var policy = new ApiPipelineRateLimitPolicy
        {
            Name = "strict",
            Kind = RateLimiterKind.TokenBucket,
            PermitLimit = 2,
            TokensPerPeriod = 1,
            Window = TimeSpan.FromMinutes(1),
        };
        using var limiter = new RedisTokenBucketRateLimiter(RedisTestHost.LimiterPartition, policy, context);

        (await limiter.AcquireAsync(1, ct)).IsAcquired.Should().BeTrue();
        (await limiter.AcquireAsync(1, ct)).IsAcquired.Should().BeTrue();
        (await limiter.AcquireAsync(1, ct)).IsAcquired.Should().BeFalse("the bucket is drained");

        await redis.Multiplexer.GetDatabase().HashIncrementAsync(
            RedisKeys.For(context.KeyPrefix, RedisTestHost.LimiterPartition), "ts", -60_000);

        // Another pod: this one remembers its rejection until the reported retry-after.
        using var otherPod = new RedisTokenBucketRateLimiter(RedisTestHost.LimiterPartition, policy, context);
        (await otherPod.AcquireAsync(1, ct)).IsAcquired.Should().BeTrue("one period passed, one token returned");
        (await otherPod.AcquireAsync(1, ct)).IsAcquired.Should().BeFalse("only one token returned");
    }

    /// <summary>
    /// Another process (or a past version) left a plain string at the bucket's key. Redis answers
    /// WRONGTYPE; that must count as a failed store and use the per-pod limiter, not surface as a
    /// 500. Once the threshold is reached the outage is logged once.
    /// </summary>
    [Fact]
    public async Task TokenBucket_KeyOfWrongType_FallsBackInsteadOf500()
    {
        var prefix = RedisTestHost.UniquePrefix();
        var logs = new CapturingLoggerProvider();
        var config = RedisTestHost.TokenBucket(prefix, permitLimit: 5, tokensPerPeriod: 1, windowSeconds: 60);
        await using var app = await RedisTestHost.CreateAsync(redis.ConnectionString, config, logs: logs);

        (await app.GetStatusAsync()).Should().Be(HttpStatusCode.OK);

        var keys = new List<RedisKey>();
        await foreach (var key in redis.Multiplexer.GetServers()[0].KeysAsync(pattern: prefix + "*"))
        {
            keys.Add(key);
        }

        keys.Should().ContainSingle("one request wrote one bucket");
        await redis.Multiplexer.GetDatabase().StringSetAsync(keys[0], "not-a-hash");

        (await app.GetStatusAsync()).Should().Be(HttpStatusCode.OK, "the per-pod limiter decides");

        List<string> messages;
        lock (logs.Messages)
        {
            messages = [.. logs.Messages];
        }

        messages.Count(m => m.Contains(UnavailableMessage, StringComparison.Ordinal)).Should().Be(1);
    }
}
