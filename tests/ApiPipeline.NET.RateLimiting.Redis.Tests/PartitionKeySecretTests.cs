using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Options;

namespace ApiPipeline.NET.RateLimiting.Redis.Tests;

/// <summary>
/// Partition keys hold user ids and client addresses. An unkeyed hash of them can be reversed by
/// anyone who can list the keys: hash all 2^32 IPv4 addresses, or a list of guessed user ids, and
/// compare. With <c>PartitionKeySecret</c> the key is an HMAC under a secret every pod shares and
/// Redis never sees, so the keys reveal nothing without it.
/// </summary>
public sealed class PartitionKeySecretTests(RedisFixture redis)
{
    private const string Secret = "0123456789abcdef0123456789abcdef-test-secret";
    private const string Address = "203.0.113.7";

    private static byte[] Bytes(string secret) => Encoding.UTF8.GetBytes(secret);

    /// <summary>
    /// The key depends on the secret: without it, hashing a guessed address does not find the key.
    /// It stays deterministic, so every pod with the same secret derives the same key.
    /// </summary>
    [Fact]
    public void KeyedHash_DependsOnTheSecret_AndIsDeterministic()
    {
        var keyed = RedisKeys.For("p", Address, Bytes(Secret));

        keyed.Should().Be(RedisKeys.For("p", Address, Bytes(Secret)), "pods sharing the secret share the key");
        keyed.Should().NotBe(RedisKeys.For("p", Address, partitionKeySecret: null), "an unkeyed hash of the address must not find it");
        keyed.Should().NotBe(RedisKeys.For("p", Address, Bytes(Secret + "x")));
        keyed.ToString().Should().MatchRegex("^p:apipipeline:rl:\\{[0-9a-f]{32}\\}$", "the key keeps its shape and fixed length");
    }

    /// <summary>A limiter with the secret writes the keyed key, and nothing under the unkeyed one.</summary>
    [Fact]
    public async Task Limiter_WithTheSecret_WritesTheKeyedKey()
    {
        var prefix = RedisTestHost.UniquePrefix();
        var (provider, context) = RedisTestHost.CreateLimiterContext(
            _ => redis.Multiplexer, keyPrefix: prefix, partitionKeySecret: Secret);
        await using var _ = provider;
        using var limiter = new RedisFixedWindowRateLimiter(RedisTestHost.LimiterPartition, RedisTestHost.LimiterPolicy, context);

        using var lease = await limiter.AcquireAsync(1, TestContext.Current.CancellationToken);
        lease.IsAcquired.Should().BeTrue();

        var db = redis.Multiplexer.GetDatabase();
        (await db.KeyExistsAsync(RedisKeys.For(prefix, RedisTestHost.LimiterPartition, Bytes(Secret)))).Should().BeTrue();
        (await db.KeyExistsAsync(RedisKeys.For(prefix, RedisTestHost.LimiterPartition, partitionKeySecret: null))).Should().BeFalse();
    }

    /// <summary>Two pods configured with the same secret still share one bucket.</summary>
    [Fact]
    public async Task PodsWithTheSameSecret_ShareTheBucket()
    {
        var config = RedisTestHost.FixedWindow(RedisTestHost.UniquePrefix(), permitLimit: 1);
        config["ApiPipeline:RateLimiting:Redis:PartitionKeySecret"] = Secret;
        await using var podA = await RedisTestHost.CreateAsync(redis.ConnectionString, config);
        await using var podB = await RedisTestHost.CreateAsync(redis.ConnectionString, config);

        (await podA.GetStatusAsync()).Should().Be(HttpStatusCode.OK);
        ((int)await podB.GetStatusAsync()).Should().Be(429);
    }

    /// <summary>
    /// Outside Development the secret is required with sharing on, as the key prefix is: the keys
    /// would otherwise identify callers to anyone with read access to Redis.
    /// </summary>
    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public async Task Unset_OutsideDevelopment_FailsStartup(string environment)
    {
        await using var app = Build(environment, secret: null);

        var start = async () => await app.StartAsync(TestContext.Current.CancellationToken);

        (await start.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("PartitionKeySecret is required outside Development").And.Contain(environment);
    }

    /// <summary>With the secret set the same host starts.</summary>
    [Fact]
    public async Task Set_OutsideDevelopment_Starts()
    {
        await using var app = Build("Production", Secret);

        var start = async () => await app.StartAsync(TestContext.Current.CancellationToken);

        await start.Should().NotThrowAsync();
    }

    /// <summary>
    /// A short secret can be brute-forced together with the address space; at least 32 characters.
    /// The message never repeats the value.
    /// </summary>
    [Fact]
    public async Task TooShort_FailsStartup_WithoutEchoingIt()
    {
        await using var app = Build("Production", "short-secret-value");

        var start = async () => await app.StartAsync(TestContext.Current.CancellationToken);

        (await start.Should().ThrowAsync<OptionsValidationException>())
            .Which.Message.Should().Contain("PartitionKeySecret").And.NotContain("short-secret-value");
    }

    private static WebApplication Build(string environment, string? secret)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ApiPipeline:RateLimiting:Redis:Enabled"] = "true",
            ["ApiPipeline:RateLimiting:Redis:Configuration"] = "127.0.0.1:1,abortConnect=false",
            ["ApiPipeline:RateLimiting:Redis:KeyPrefix"] = "orders-api:prod",
            ["ApiPipeline:RateLimiting:Redis:PartitionKeySecret"] = secret,
        });
        builder.Services.AddApiPipelineRedisRateLimiting(builder.Configuration);
        return builder.Build();
    }
}
