using DotNet.Testcontainers.Builders;
using StackExchange.Redis;
using Testcontainers.Redis;

namespace ApiPipeline.NET.RateLimiting.Redis.Tests;

/// <summary>
/// One real Redis in Docker for the assembly: only Redis runs the Lua scripts. Without Docker a
/// local run skips the tests; on CI (<c>CI=true</c>) it fails.
/// </summary>
public sealed class RedisFixture : IAsyncLifetime
{
    private RedisContainer? _container;
    private ConnectionMultiplexer? _multiplexer;

    /// <summary>Why the Redis tests are skipped on this machine, or <c>null</c> when Docker is available.</summary>
    public static string? UnavailableReason { get; private set; }

    /// <summary>The StackExchange.Redis connection string of the running container.</summary>
    public string ConnectionString => Container.GetConnectionString();

    /// <summary>
    /// The container itself, so an outage test can pause and unpause Redis. A test that pauses it
    /// must unpause it in <c>finally</c>: every later test shares this container.
    /// </summary>
    public RedisContainer Container =>
        _container ?? throw new InvalidOperationException("The fixture has not been initialized.");

    /// <summary>
    /// A connection owned by the fixture, for inspecting keys and for tests that pass a connection
    /// in code.
    /// </summary>
    public IConnectionMultiplexer Multiplexer =>
        _multiplexer ?? throw new InvalidOperationException("The fixture has not been initialized.");

    /// <summary>Starts the container and connects to it, or records why it cannot.</summary>
    public async ValueTask InitializeAsync()
    {
        try
        {
            _container = new RedisBuilder(TestImages.Redis).Build();
            await _container.StartAsync();
        }
        catch (DockerUnavailableException ex) when (!IsCi())
        {
            UnavailableReason =
                $"Docker is not available ({ex.Message.Split('\n')[0].Trim()}). These tests run against a " +
                "real Redis container: start Docker to run them. CI fails instead of skipping.";
            return;
        }

        _multiplexer = await ConnectionMultiplexer.ConnectAsync(ConnectionString);
    }

    /// <summary>Disconnects and removes the container.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_multiplexer is not null)
        {
            await _multiplexer.DisposeAsync();
        }

        if (_container is not null && UnavailableReason is null)
        {
            await _container.DisposeAsync();
        }
    }

    private static bool IsCi() =>
        string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase);
}
