using System.Globalization;
using System.Net;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.TestHost;
using StackExchange.Redis;

namespace ApiPipeline.NET.RateLimiting.Redis.Tests;

/// <summary>
/// Builds and starts one API replica on <see cref="TestServer"/>: the full pipeline plus the Redis
/// add-on, with rate limiting on and one <c>GET /test</c> endpoint. Two hosts built with the same
/// key prefix behave like two pods of one API.
/// </summary>
internal static class RedisTestHost
{
    /// <summary>
    /// Builds and starts a host. <paramref name="config"/> is applied over the defaults, so a test
    /// can override or clear (with <c>null</c>) any of them.
    /// </summary>
    /// <param name="connectionString">Default for <c>ApiPipeline:RateLimiting:Redis:Configuration</c>.</param>
    /// <param name="config">The test's settings.</param>
    /// <param name="applicationName">The host's application name (part of the default key prefix).</param>
    /// <param name="logs">Captures the host's log records when given.</param>
    /// <param name="configureServices">Runs after the default registrations.</param>
    /// <param name="configureApp">Maps extra endpoints, after <c>GET /test</c>.</param>
    /// <param name="registerRedisFirst">
    /// Calls <c>AddApiPipelineRedisRateLimiting</c> before <c>AddApiPipeline</c> instead of after.
    /// </param>
    public static async Task<WebApplication> CreateAsync(
        string connectionString,
        Dictionary<string, string?> config,
        string applicationName = "api-a",
        CapturingLoggerProvider? logs = null,
        Action<IServiceCollection, IConfiguration>? configureServices = null,
        Action<WebApplication>? configureApp = null,
        bool registerRedisFirst = false)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = applicationName,
            EnvironmentName = Environments.Development,
        });

        builder.WebHost.UseTestServer();

        var settings = new Dictionary<string, string?>
        {
            ["ApiPipeline:RateLimiting:Enabled"] = "true",
            ["ApiPipeline:RateLimiting:DefaultPolicy"] = "strict",
            // TestServer sets no RemoteIpAddress: one shared anonymous bucket, not the Reject guard.
            ["ApiPipeline:RateLimiting:AnonymousFallback"] = "RateLimit",
            ["ApiPipeline:RateLimiting:Redis:Configuration"] = connectionString,
            // Generous, so a slow connect on a busy CI runner is not taken for an outage. A test
            // about timeouts sets its own.
            ["ApiPipeline:RateLimiting:Redis:CommandTimeout"] = "00:00:05",
            // Most tests are about what happens once the circuit opens, so the first failure opens
            // it. Tests of the threshold itself set their own.
            ["ApiPipeline:RateLimiting:Redis:FailureThreshold"] = "1",
            ["ApiPipeline:ResponseCompression:Enabled"] = "false",
            ["ApiPipeline:ResponseCaching:Enabled"] = "false",
            ["ApiPipeline:SecurityHeaders:Enabled"] = "false",
            ["ApiPipeline:Cors:Enabled"] = "false",
            ["ApiPipeline:OutputCaching:Enabled"] = "false",
        };

        foreach (var (key, value) in config)
        {
            settings[key] = value;
        }

        builder.Configuration.AddInMemoryCollection(settings);

        if (logs is not null)
        {
            builder.Logging.AddProvider(logs);
        }

        builder.Services.AddAuthentication(TestAuthHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });
        builder.Services.AddAuthorization();
        if (registerRedisFirst)
        {
            builder.Services.AddApiPipelineRedisRateLimiting(builder.Configuration);
            builder.Services.AddApiPipeline(builder.Configuration);
        }
        else
        {
            builder.Services.AddApiPipeline(builder.Configuration);
            builder.Services.AddApiPipelineRedisRateLimiting(builder.Configuration);
        }

        configureServices?.Invoke(builder.Services, builder.Configuration);

        var app = builder.Build();
        try
        {
            app.UseApiPipeline(p => p.WithAuthentication().WithRateLimiting());
            app.MapGet("/test", () => Results.Ok("ok"));
            configureApp?.Invoke(app);
            await app.StartAsync(TestContext.Current.CancellationToken);
            return app;
        }
        catch
        {
            await app.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Settings for one fixed window policy named <c>strict</c>, shared through Redis under
    /// <paramref name="keyPrefix"/> (the default prefix when <c>null</c>).
    /// </summary>
    public static Dictionary<string, string?> FixedWindow(
        string? keyPrefix,
        int permitLimit,
        int windowSeconds = 60,
        bool redisEnabled = true)
    {
        var config = new Dictionary<string, string?>
        {
            ["ApiPipeline:RateLimiting:Redis:Enabled"] = redisEnabled ? "true" : "false",
            ["ApiPipeline:RateLimiting:Policies:0:Name"] = "strict",
            ["ApiPipeline:RateLimiting:Policies:0:Kind"] = "FixedWindow",
            ["ApiPipeline:RateLimiting:Policies:0:PermitLimit"] = permitLimit.ToString(CultureInfo.InvariantCulture),
            ["ApiPipeline:RateLimiting:Policies:0:Window"] = TimeSpan.FromSeconds(windowSeconds).ToString("c", CultureInfo.InvariantCulture),
        };

        if (keyPrefix is not null)
        {
            config["ApiPipeline:RateLimiting:Redis:KeyPrefix"] = keyPrefix;
        }

        return config;
    }

    /// <summary>
    /// Settings for one sliding window policy named <c>strict</c>, shared through Redis under
    /// <paramref name="keyPrefix"/>, with no queueing.
    /// </summary>
    public static Dictionary<string, string?> SlidingWindow(
        string? keyPrefix,
        int permitLimit,
        int windowSeconds,
        int segmentsPerWindow)
    {
        var config = FixedWindow(keyPrefix, permitLimit, windowSeconds);
        config["ApiPipeline:RateLimiting:Policies:0:Kind"] = "SlidingWindow";
        config["ApiPipeline:RateLimiting:Policies:0:SegmentsPerWindow"] = segmentsPerWindow.ToString(CultureInfo.InvariantCulture);
        config["ApiPipeline:RateLimiting:Policies:0:QueueLimit"] = "0";
        return config;
    }

    /// <summary>
    /// Settings for one token bucket policy named <c>strict</c>, shared through Redis under
    /// <paramref name="keyPrefix"/>, with no queueing.
    /// </summary>
    public static Dictionary<string, string?> TokenBucket(
        string? keyPrefix,
        int permitLimit,
        int tokensPerPeriod,
        int windowSeconds)
    {
        var config = FixedWindow(keyPrefix, permitLimit, windowSeconds);
        config["ApiPipeline:RateLimiting:Policies:0:Kind"] = "TokenBucket";
        config["ApiPipeline:RateLimiting:Policies:0:TokensPerPeriod"] = tokensPerPeriod.ToString(CultureInfo.InvariantCulture);
        config["ApiPipeline:RateLimiting:Policies:0:QueueLimit"] = "0";
        return config;
    }

    /// <summary>
    /// Settings for one concurrency policy named <c>strict</c>, shared through Redis under
    /// <paramref name="keyPrefix"/>, with no queueing.
    /// </summary>
    /// <param name="keyPrefix">The key prefix.</param>
    /// <param name="permitLimit">The requests allowed in flight at once, across hosts.</param>
    /// <param name="leaseTimeoutSeconds">
    /// <c>ApiPipeline:RateLimiting:Redis:ConcurrencyLeaseTimeout</c> (the default when <c>null</c>).
    /// </param>
    public static Dictionary<string, string?> Concurrency(string? keyPrefix, int permitLimit, int? leaseTimeoutSeconds = null)
    {
        var config = FixedWindow(keyPrefix, permitLimit);
        config.Remove("ApiPipeline:RateLimiting:Policies:0:Window");
        config["ApiPipeline:RateLimiting:Policies:0:Kind"] = "Concurrency";
        config["ApiPipeline:RateLimiting:Policies:0:QueueLimit"] = "0";
        if (leaseTimeoutSeconds is { } seconds)
        {
            config["ApiPipeline:RateLimiting:Redis:ConcurrencyLeaseTimeout"] = TimeSpan.FromSeconds(seconds).ToString("c", CultureInfo.InvariantCulture);
        }

        return config;
    }

    /// <summary>The partition key of <see cref="CreateLimiter"/>'s limiter.</summary>
    public const string LimiterPartition = "partition";

    /// <summary>The policy of <see cref="CreateLimiter"/>'s limiter: 5 per minute, named <c>strict</c>.</summary>
    public static ApiPipelineRateLimitPolicy LimiterPolicy => new() { Name = "strict", PermitLimit = 5, Window = TimeSpan.FromSeconds(60) };

    /// <summary>
    /// A limiter over <paramref name="connection"/> with a 250 ms command timeout, built directly so
    /// a test can time the acquire alone. The provider holds the circuit and connection; dispose it.
    /// </summary>
    /// <param name="connection">Returns the connection to use.</param>
    /// <param name="logs">Captures the circuit's log records when given.</param>
    /// <param name="keyPrefix">The key prefix (a unique one when <c>null</c>).</param>
    /// <param name="failureThreshold">Consecutive failures that open the circuit (the first, by default).</param>
    /// <param name="timeProvider">The clock of the command timeout and the circuit (the system clock by default).</param>
    public static (ServiceProvider Provider, RedisFixedWindowRateLimiter Limiter) CreateLimiter(
        Func<IServiceProvider, IConnectionMultiplexer> connection,
        CapturingLoggerProvider? logs = null,
        string? keyPrefix = null,
        int failureThreshold = 1,
        TimeProvider? timeProvider = null)
    {
        var (provider, context) = CreateLimiterContext(connection, logs, keyPrefix, failureThreshold, timeProvider);
        return (provider, new RedisFixedWindowRateLimiter(LimiterPartition, LimiterPolicy, context));
    }

    /// <summary>
    /// A limiter context over <paramref name="connection"/>: 250 ms command timeout, circuit opening
    /// on the first failure, 300 s lease timeout, unkeyed hashes and no lease maximum unless given.
    /// </summary>
    public static (ServiceProvider Provider, RedisRateLimiterContext Context) CreateLimiterContext(
        Func<IServiceProvider, IConnectionMultiplexer> connection,
        CapturingLoggerProvider? logs = null,
        string? keyPrefix = null,
        int failureThreshold = 1,
        TimeProvider? timeProvider = null,
        SynchronousAttemptMonitor? synchronousAttempts = null,
        TimeSpan? concurrencyLeaseTimeout = null,
        TimeSpan? commandTimeout = null,
        string? partitionKeySecret = null,
        TimeSpan? concurrencyLeaseMaxDuration = null)
    {
        timeProvider ??= TimeProvider.System;
        var services = new ServiceCollection();
        services.AddSingleton(timeProvider);
        if (logs is not null)
        {
            services.AddLogging(logging => logging.AddProvider(logs));
        }

        services.AddApiPipelineRedisRateLimiting(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ApiPipeline:RateLimiting:Redis:Enabled"] = "true",
                ["ApiPipeline:RateLimiting:Redis:CommandTimeout"] = "00:00:00.250",
                ["ApiPipeline:RateLimiting:Redis:FailureThreshold"] =
                    failureThreshold.ToString(CultureInfo.InvariantCulture),
            }).Build(),
            connection);
        var provider = services.BuildServiceProvider();

        var context = new RedisRateLimiterContext(
            provider.GetRequiredService<RedisConnectionSource>(),
            keyPrefix ?? UniquePrefix(),
            commandTimeout ?? TimeSpan.FromMilliseconds(250),
            timeProvider,
            new InMemoryRateLimiterFactory(),
            provider.GetRequiredService<RedisCircuit>(),
            provider.GetRequiredService<RedisRateLimitingMetrics>(),
            concurrencyLeaseTimeout ?? TimeSpan.FromSeconds(300),
            synchronousAttempts: synchronousAttempts,
            partitionKeySecret: partitionKeySecret is null ? null : System.Text.Encoding.UTF8.GetBytes(partitionKeySecret),
            concurrencyLeaseMaxDuration: concurrencyLeaseMaxDuration);
        return (provider, context);
    }

    /// <summary>A key prefix no other test uses, so tests never share a bucket.</summary>
    public static string UniquePrefix() => "test-" + Guid.NewGuid().ToString("N");

    /// <summary>Sends <c>GET /test</c> (or <paramref name="path"/>) and returns the status code.</summary>
    public static async Task<HttpStatusCode> GetStatusAsync(this WebApplication app, string? user = null, string path = "/test")
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (user is not null)
        {
            request.Headers.Add(TestAuthHandler.UserHeader, user);
        }

        using var client = app.GetTestClient();
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        return response.StatusCode;
    }
}
