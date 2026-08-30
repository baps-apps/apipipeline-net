using FluentAssertions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using StackExchange.Redis;

namespace ApiPipeline.NET.RateLimiting.Redis.Tests;

/// <summary>
/// The server check warns, outside Development, when the connection reaches Redis on another host
/// without TLS: the partition keys' hashes, the scripts and any password would cross the network in
/// plain text. A loopback endpoint (a pod-local TLS proxy) is not reported.
/// </summary>
public sealed class RedisTlsCheckTests
{
    private const string TlsWarning = "without TLS";

    /// <summary>The rule, apart from any connection.</summary>
    [Theory]
    [InlineData("redis.internal:6379", true)]
    [InlineData("10.0.0.5:6380", true)]
    [InlineData("redis.internal:6379,ssl=true", false)]
    [InlineData("127.0.0.1:6379", false)]
    [InlineData("localhost:6379", false)]
    [InlineData("[::1]:6379", false)]
    [InlineData("127.0.0.1:6379,redis.internal:6379", true)]
    public void PlainTextOffHost(string configuration, bool expected) =>
        RedisServerCheck.SendsPlainTextOffHost(ConfigurationOptions.Parse(configuration)).Should().Be(expected);

    /// <summary>A plain-text connection to another host is reported outside Development, naming the endpoint.</summary>
    [Fact]
    public async Task PlainTextRemote_IsReported_OutsideDevelopment()
    {
        var messages = await CheckAsync("redis.invalid:6379", Environments.Production);

        messages.Should().ContainSingle(m => m.Contains(TlsWarning, StringComparison.Ordinal))
            .Which.Should().Contain("redis.invalid:6379");
    }

    /// <summary>The connection's configuration holds the password; the warning names only the endpoints.</summary>
    [Fact]
    public async Task Warning_NeverIncludesThePassword()
    {
        const string Password = "Pa55w0rd-never-logged";

        var messages = await CheckAsync($"redis.invalid:6379,password={Password},user=limiter", Environments.Production);

        messages.Should().Contain(m => m.Contains(TlsWarning, StringComparison.Ordinal));
        messages.Should().NotContain(m => m.Contains(Password, StringComparison.Ordinal));
    }

    /// <summary>TLS on, or Development: nothing is reported.</summary>
    [Theory]
    [InlineData("redis.invalid:6379,ssl=true", "Production")]
    [InlineData("redis.invalid:6379", "Development")]
    public async Task TlsOrDevelopment_IsNotReported(string configuration, string environment)
    {
        var messages = await CheckAsync(configuration, environment);

        messages.Should().NotContain(m => m.Contains(TlsWarning, StringComparison.Ordinal));
    }

    /// <summary>
    /// Runs the check on a real, never-connected multiplexer (the host does not resolve), so only its
    /// configuration is read; returns what was logged.
    /// </summary>
    private static async Task<List<string>> CheckAsync(string configuration, string environment)
    {
        var logs = new CapturingLoggerProvider();
        using var factory = LoggerFactory.Create(l => l.AddProvider(logs));
        await using var multiplexer = await ConnectionMultiplexer.ConnectAsync(
            $"{configuration},abortConnect=false,connectTimeout=100,connectRetry=0");

        var check = new RedisServerCheck(
            factory.CreateLogger<RedisServerCheck>(),
            new HostingEnvironment { EnvironmentName = environment });
        await check.CheckAsync(multiplexer);

        lock (logs.Messages)
        {
            return [.. logs.Messages];
        }
    }
}
