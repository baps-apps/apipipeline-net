using System.Threading.RateLimiting;
using FluentAssertions;
using StackExchange.Redis;

namespace ApiPipeline.NET.RateLimiting.Redis.Tests;

/// <summary>
/// Each decision is one Lua script, so it is atomic on the server: however many pods ask at once,
/// exactly the limit is admitted. A read-then-write done in two round trips would admit more under
/// contention; these tests make the contention real.
/// </summary>
public sealed class AtomicityTests(RedisFixture redis)
{
    private const int Pods = 4;
    private const int RequestsPerPod = 100;
    private const int Limit = 37;

    /// <summary>
    /// 400 acquires at once, from four pods with a connection each, against one partition: exactly
    /// the limit is admitted, for every kind. No pod falls back (the per-pod limiters would admit
    /// more): the command timeout is generous and every circuit stays closed.
    /// </summary>
    [Theory]
    [InlineData(RateLimiterKind.FixedWindow)]
    [InlineData(RateLimiterKind.SlidingWindow)]
    [InlineData(RateLimiterKind.TokenBucket)]
    [InlineData(RateLimiterKind.Concurrency)]
    public async Task ConcurrentAcquires_FromManyPods_AdmitExactlyTheLimit(RateLimiterKind kind)
    {
        var ct = TestContext.Current.CancellationToken;
        var prefix = RedisTestHost.UniquePrefix();
        var policy = new ApiPipelineRateLimitPolicy
        {
            Name = "strict",
            Kind = kind,
            PermitLimit = Limit,
            Window = TimeSpan.FromMinutes(10),
            SegmentsPerWindow = 4,
            TokensPerPeriod = 1,
            QueueLimit = 0,
        };

        var connections = new List<ConnectionMultiplexer>();
        var providers = new List<ServiceProvider>();
        var limiters = new List<RateLimiter>();
        var leases = new List<RateLimitLease>();
        try
        {
            for (var pod = 0; pod < Pods; pod++)
            {
                var connection = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
                connections.Add(connection);
                var (provider, context) = RedisTestHost.CreateLimiterContext(
                    _ => connection, keyPrefix: prefix, failureThreshold: 1000, commandTimeout: TimeSpan.FromSeconds(10));
                providers.Add(provider);
                limiters.Add(kind switch
                {
                    RateLimiterKind.FixedWindow => new RedisFixedWindowRateLimiter(RedisTestHost.LimiterPartition, policy, context),
                    RateLimiterKind.SlidingWindow => new RedisSlidingWindowRateLimiter(RedisTestHost.LimiterPartition, policy, context),
                    RateLimiterKind.TokenBucket => new RedisTokenBucketRateLimiter(RedisTestHost.LimiterPartition, policy, context),
                    _ => new RedisConcurrencyLimiter(RedisTestHost.LimiterPartition, policy, context),
                });
            }

            using var start = new ManualResetEventSlim();
            var attempts = limiters
                .SelectMany(limiter => Enumerable.Range(0, RequestsPerPod).Select(_ => Task.Run(async () =>
                {
                    start.Wait(ct);
                    return await limiter.AcquireAsync(1, ct);
                }, ct)))
                .ToList();
            start.Set();
            leases.AddRange(await Task.WhenAll(attempts));

            leases.Count(l => l.IsAcquired).Should().Be(Limit, "the script decides atomically, whatever the contention");
            providers.Should().OnlyContain(
                p => !p.GetRequiredService<RedisCircuit>().IsOpen, "every decision came from Redis, none from a per-pod fallback");
        }
        finally
        {
            // Concurrency leases are held until here, so the count above is of slots held at once.
            leases.ForEach(l => l.Dispose());
            limiters.ForEach(l => l.Dispose());
            foreach (var provider in providers)
            {
                await provider.DisposeAsync();
            }

            foreach (var connection in connections)
            {
                await connection.DisposeAsync();
            }
        }
    }
}
