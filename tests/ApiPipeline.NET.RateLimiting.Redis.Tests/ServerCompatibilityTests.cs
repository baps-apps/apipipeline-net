using System.Net;
using System.Net.Sockets;
using System.Threading.RateLimiting;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using FluentAssertions;
using StackExchange.Redis;
using Testcontainers.Redis;

namespace ApiPipeline.NET.RateLimiting.Redis.Tests;

/// <summary>
/// The README promises Redis 5.0 or later and Redis Cluster. Every other test runs on one Redis
/// 7.4; these run every limiter kind, and the renewal script, on the oldest supported version and
/// on a cluster.
/// </summary>
public sealed class ServerCompatibilityTests(
    ServerCompatibilityTests.OldestRedis oldest,
    ServerCompatibilityTests.ClusterRedis cluster)
    : IClassFixture<ServerCompatibilityTests.OldestRedis>, IClassFixture<ServerCompatibilityTests.ClusterRedis>
{
    /// <summary>Redis 5.0, the oldest version the README supports.</summary>
    [Fact]
    public Task Redis50_RunsEveryScript() => ExerciseEveryKindAsync(oldest.Multiplexer);

    /// <summary>
    /// A Redis Cluster (one node holding every slot, so it runs in a container): cluster mode rejects
    /// a script whose keys span slots, and each partition's key carries a hash tag.
    /// </summary>
    [Fact]
    public async Task Cluster_RunsEveryScript()
    {
        cluster.Multiplexer.GetServers().Should().OnlyContain(s => s.ServerType == ServerType.Cluster);

        await ExerciseEveryKindAsync(cluster.Multiplexer);
    }

    private static async Task ExerciseEveryKindAsync(IConnectionMultiplexer multiplexer)
    {
        var ct = TestContext.Current.CancellationToken;
        foreach (var kind in Enum.GetValues<RateLimiterKind>())
        {
            var (provider, context) = RedisTestHost.CreateLimiterContext(
                _ => multiplexer, commandTimeout: TimeSpan.FromSeconds(5));
            await using var _ = provider;
            var policy = new ApiPipelineRateLimitPolicy
            {
                Name = "strict",
                Kind = kind,
                PermitLimit = 2,
                Window = TimeSpan.FromMinutes(1),
                SegmentsPerWindow = 4,
                TokensPerPeriod = 1,
                QueueLimit = 0,
            };
            using RateLimiter limiter = kind switch
            {
                RateLimiterKind.FixedWindow => new RedisFixedWindowRateLimiter(RedisTestHost.LimiterPartition, policy, context),
                RateLimiterKind.SlidingWindow => new RedisSlidingWindowRateLimiter(RedisTestHost.LimiterPartition, policy, context),
                RateLimiterKind.TokenBucket => new RedisTokenBucketRateLimiter(RedisTestHost.LimiterPartition, policy, context),
                _ => new RedisConcurrencyLimiter(RedisTestHost.LimiterPartition, policy, context),
            };

            using var first = await limiter.AcquireAsync(1, ct);
            using var second = await limiter.AcquireAsync(1, ct);
            using var third = await limiter.AcquireAsync(1, ct);

            first.IsAcquired.Should().BeTrue($"{kind}: the first permit is admitted");
            second.IsAcquired.Should().BeTrue($"{kind}: the second permit is admitted");
            third.IsAcquired.Should().BeFalse($"{kind}: the limit of 2 is reached");
            context.Circuit.IsOpen.Should().BeFalse($"{kind}: every answer came from Redis");

            var key = RedisKeys.For(context.KeyPrefix, RedisTestHost.LimiterPartition);
            var ttl = (long)await multiplexer.GetDatabase().ExecuteAsync("PTTL", key.ToString());
            ttl.Should().BePositive($"{kind}: the key expires");

            if (kind == RateLimiterKind.Concurrency)
            {
                var members = await multiplexer.GetDatabase().SortedSetRangeByRankAsync(key);
                var renewed = (long)await multiplexer.GetDatabase().ScriptEvaluateAsync(
                    RedisScripts.ConcurrencyRenew, [key], [120_000, .. members]);
                renewed.Should().Be(2, "both held slots are renewed");
            }
        }
    }

    /// <summary>The oldest Redis the README supports.</summary>
    public sealed class OldestRedis : IAsyncLifetime
    {
        private readonly RedisContainer _container = new RedisBuilder(TestImages.OldestRedis).Build();

        /// <summary>A connection to it.</summary>
        public IConnectionMultiplexer Multiplexer { get; private set; } = null!;

        /// <inheritdoc />
        public async ValueTask InitializeAsync()
        {
            await _container.StartAsync();
            Multiplexer = await ConnectionMultiplexer.ConnectAsync(_container.GetConnectionString());
        }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            await Multiplexer.DisposeAsync();
            await _container.DisposeAsync();
        }
    }

    /// <summary>
    /// A one-node Redis Cluster. The node announces 127.0.0.1 and listens on the same port inside
    /// the container as on the host, so the addresses in its cluster map are reachable from here.
    /// </summary>
    public sealed class ClusterRedis : IAsyncLifetime
    {
        private readonly int _port = FreePort();
        private readonly IContainer _container;

        /// <summary>Starts nothing yet; see <see cref="InitializeAsync"/>.</summary>
        public ClusterRedis()
        {
            _container = new ContainerBuilder(TestImages.Redis)
                .WithPortBinding(_port, _port)
                .WithCommand(
                    "redis-server", "--port", _port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "--cluster-enabled", "yes", "--cluster-announce-ip", "127.0.0.1")
                .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Ready to accept connections"))
                .Build();
        }

        /// <summary>A cluster-mode connection to it.</summary>
        public IConnectionMultiplexer Multiplexer { get; private set; } = null!;

        /// <inheritdoc />
        public async ValueTask InitializeAsync()
        {
            try
            {
                await _container.StartAsync();
            }
            catch (Exception ex)
            {
                var (stdout, stderr) = await _container.GetLogsAsync();
                throw new InvalidOperationException($"The cluster node did not start. Its log: {stdout}{stderr}", ex);
            }

            var port = _port.ToString(System.Globalization.CultureInfo.InvariantCulture);
            await _container.ExecAsync(["redis-cli", "-p", port, "CLUSTER", "ADDSLOTSRANGE", "0", "16383"]);

            for (var i = 0; i < 100; i++)
            {
                var info = await _container.ExecAsync(["redis-cli", "-p", port, "CLUSTER", "INFO"]);
                if (info.Stdout.Contains("cluster_state:ok", StringComparison.Ordinal))
                {
                    break;
                }

                await Task.Delay(100);
            }

            Multiplexer = await ConnectionMultiplexer.ConnectAsync($"127.0.0.1:{port}");
        }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            if (Multiplexer is not null)
            {
                await Multiplexer.DisposeAsync();
            }

            await _container.DisposeAsync();
        }

        // A free port no higher than 55535: the cluster bus listens 10000 above it. (An ephemeral
        // port, as port 0 gives, can be higher.)
        private static int FreePort()
        {
            for (var attempt = 0; attempt < 100; attempt++)
            {
                var port = Random.Shared.Next(20_000, 55_535);
                try
                {
                    using var listener = new TcpListener(IPAddress.Loopback, port);
                    listener.Start();
                    return port;
                }
                catch (SocketException)
                {
                    // Taken; try another.
                }
            }

            throw new InvalidOperationException("No free port below 55535 for the cluster node.");
        }
    }
}
