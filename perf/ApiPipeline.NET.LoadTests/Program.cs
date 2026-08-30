using System.Globalization;
using System.Net;
using ApiPipeline.NET.LoadTests;
using ApiPipeline.NET.Performance;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.Configuration;
using StackExchange.Redis;
using Testcontainers.Redis;

if (args is ["--runtime-version"])
{
    Console.WriteLine(Environment.Version);
    return;
}

var profile = LoadTestProfile.Parse(ProfileArgument(args));
RedisContainer? redisContainer = null;

try
{
    string? redisConnection = null;
    if (profile.UsesRedis)
    {
        var runId = Environment.GetEnvironmentVariable("APIPIPELINE_LOAD_RUN_ID")
            ?? Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        var redisBuilder = new RedisBuilder(PerformanceImages.Redis)
            .WithLabel("apipipeline.load-test.run", runId);
        if (Environment.GetEnvironmentVariable("DOCKER_HOST") is { Length: > 0 } dockerHost)
        {
            redisBuilder = redisBuilder.WithDockerEndpoint(dockerHost);
        }

        redisContainer = redisBuilder.Build();
        try
        {
            await redisContainer.StartAsync();
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                "Docker is required for the Redis load-test profile.", exception);
        }

        redisConnection = redisContainer.GetConnectionString();
        EnsureLoopbackRedis(redisConnection);
    }

    var builder = WebApplication.CreateBuilder(args);
    builder.WebHost.UseUrls(ValidatedLoopbackUrls(builder.Configuration["urls"]));

    if (profile.UsesPipeline)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(Settings(profile, redisConnection))
            .Build();
        builder.Services.AddApiPipeline(configuration);
        if (profile.UsesRedis)
        {
            builder.Services.AddApiPipelineRedisRateLimiting(configuration);
        }
    }

    var app = builder.Build();
    app.Use(async (context, next) =>
    {
        context.Response.OnStarting(() =>
        {
            context.Response.Headers["X-ApiPipeline-Load-Profile"] = profile.Name;
            return Task.CompletedTask;
        });
        await next(context);
    });

    if (profile.UsesPipeline)
    {
        app.UseApiPipeline(pipeline => pipeline
            .WithRequestSizeTracking()
            .WithCorrelationId()
            .WithSecurityHeaders()
            .WithRequestTimeouts()
            .WithCors()
            .WithRateLimiting()
            .WithResponseCompression()
            .WithOutputCaching());
    }

    app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
    app.MapGet("/payload", () => Results.Text(LoadTestPayload.Json, "application/json"));
    var cached = app.MapGet(
        "/cached",
        () => Results.Text(LoadTestPayload.Json, "application/json"));
    var reject = app.MapGet("/reject", () => Results.Ok(new { status = "accepted" }));
    if (profile.UsesPipeline)
    {
        cached.CacheOutput();
        reject.RequireRateLimiting("reject");
    }

    await app.RunAsync();
}
finally
{
    if (redisContainer is not null)
    {
        await redisContainer.DisposeAsync();
    }
}

static string ProfileArgument(string[] arguments)
{
    var index = Array.IndexOf(arguments, "--profile");
    if (index < 0 || index + 1 >= arguments.Length)
    {
        throw new ArgumentException(
            "The load-test host requires --profile baseline|pipeline|redis.");
    }

    return arguments[index + 1];
}

static string ValidatedLoopbackUrls(string? configuredUrls)
{
    var urls = string.IsNullOrWhiteSpace(configuredUrls)
        ? "http://127.0.0.1:0"
        : configuredUrls;
    foreach (var value in urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || !IsLoopbackHost(uri.Host))
        {
            throw new InvalidOperationException(
                $"The load-test host binds loopback only; rejected URL '{value}'.");
        }
    }

    return urls;
}

static void EnsureLoopbackRedis(string configuration)
{
    var options = ConfigurationOptions.Parse(configuration);
    if (options.EndPoints.Count == 0
        || options.EndPoints.Any(endpoint => endpoint switch
        {
            IPEndPoint ip => !IPAddress.IsLoopback(ip.Address),
            DnsEndPoint dns => !IsLoopbackHost(dns.Host),
            _ => true,
        }))
    {
        throw new InvalidOperationException(
            "The Redis load-test profile refuses non-loopback Redis endpoints.");
    }
}

static bool IsLoopbackHost(string host) =>
    string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
    || IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address);

static Dictionary<string, string?> Settings(
    LoadTestProfile profile,
    string? redisConnection)
{
    const string prefix = "ApiPipeline";
    var settings = new Dictionary<string, string?>
    {
        [$"{prefix}:RateLimiting:Enabled"] = "true",
        [$"{prefix}:RateLimiting:DefaultPolicy"] = "default",
        [$"{prefix}:RateLimiting:AnonymousFallback"] = "RateLimit",
        [$"{prefix}:RateLimiting:Policies:0:Name"] = "default",
        [$"{prefix}:RateLimiting:Policies:0:Kind"] = "FixedWindow",
        // The canonical run can exceed one million requests during warmup plus measurement.
        // Keep the limiter active so its overhead is measured, but make the allowed path
        // effectively non-exhausting; rejection has its own one-permit policy below.
        [$"{prefix}:RateLimiting:Policies:0:PermitLimit"] = int.MaxValue.ToString(CultureInfo.InvariantCulture),
        [$"{prefix}:RateLimiting:Policies:0:QueueLimit"] = "0",
        [$"{prefix}:RateLimiting:Policies:0:Window"] = "01:00:00",
        [$"{prefix}:RateLimiting:Policies:1:Name"] = "reject",
        [$"{prefix}:RateLimiting:Policies:1:Kind"] = "FixedWindow",
        [$"{prefix}:RateLimiting:Policies:1:PermitLimit"] = "1",
        [$"{prefix}:RateLimiting:Policies:1:QueueLimit"] = "0",
        [$"{prefix}:RateLimiting:Policies:1:Window"] = "01:00:00",
        [$"{prefix}:ResponseCompression:Enabled"] = "true",
        [$"{prefix}:ResponseCompression:EnableForHttps"] = "false",
        [$"{prefix}:ResponseCompression:EnableBrotli"] = "true",
        [$"{prefix}:ResponseCompression:EnableGzip"] = "true",
        [$"{prefix}:ResponseCompression:MimeTypes:0"] = "application/json",
        [$"{prefix}:OutputCaching:Enabled"] = "true",
        [$"{prefix}:OutputCaching:DefaultExpiration"] = "00:01:00",
        [$"{prefix}:OutputCaching:SizeLimitBytes"] = "52428800",
        [$"{prefix}:SecurityHeaders:Enabled"] = "true",
        [$"{prefix}:SecurityHeaders:EnableStrictTransportSecurity"] = "false",
        [$"{prefix}:Cors:Enabled"] = "true",
        [$"{prefix}:Cors:AllowedOrigins:0"] = "*",
        [$"{prefix}:RequestLimits:Enabled"] = "true",
        [$"{prefix}:RequestLimits:MaxRequestBodySize"] = "1048576",
        [$"{prefix}:RequestLimits:MaxRequestHeadersTotalSize"] = "16384",
        [$"{prefix}:RequestLimits:MaxRequestHeaderCount"] = "100",
        [$"{prefix}:RequestTimeouts:Enabled"] = "true",
        [$"{prefix}:RequestTimeouts:DefaultTimeout"] = "00:00:30",
        [$"{prefix}:RequestTimeouts:TimeoutStatusCode"] = "504",
    };

    if (profile.UsesRedis)
    {
        settings[$"{prefix}:RateLimiting:Redis:Enabled"] = "true";
        settings[$"{prefix}:RateLimiting:Redis:Configuration"] = redisConnection;
        settings[$"{prefix}:RateLimiting:Redis:CommandTimeout"] = "00:00:05";
        settings[$"{prefix}:RateLimiting:Redis:KeyPrefix"] =
            $"apipipeline-load:{Guid.NewGuid():N}";
        settings[$"{prefix}:RateLimiting:Redis:PartitionKeySecret"] =
            "load-test-partition-secret-32-bytes";
    }

    return settings;
}
