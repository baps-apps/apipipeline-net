using System.Threading.RateLimiting;
using ApiPipeline.NET.Configuration;
using ApiPipeline.NET.Middleware;
using ApiPipeline.NET.Observability;
using BenchmarkDotNet.Attributes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ApiPipeline.NET.Perf;

/// <summary>
/// Per-middleware cost, measured by invoking the built <see cref="RequestDelegate"/> against a
/// pooled <see cref="DefaultHttpContext"/>. Not through <c>TestServer</c>, whose own overhead would
/// swamp the numbers.
/// </summary>
[MemoryDiagnoser]
public class MiddlewareBenchmarks
{
    private RequestDelegate _empty = null!;
    private RequestDelegate _correlationId = null!;
    private RequestDelegate _securityHeaders = null!;
    private RequestDelegate _requestSize = null!;
    private RequestDelegate _fullPipeline = null!;
    private RequestDelegate _emptySuspending = null!;
    private RequestDelegate _securityHeadersSuspending = null!;
    private RequestDelegate _requestSizeSuspending = null!;
    private IServiceProvider _services = null!;

    /// <summary>Builds the service provider and the delegates under test.</summary>
    [GlobalSetup]
    public void Setup()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ApiPipeline:SecurityHeaders:Enabled"] = "true",
                ["ApiPipeline:CorrelationId:AcceptInboundCorrelationId"] = "true",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging(b => b.ClearProviders());
        services.AddSingleton<IConfiguration>(configuration);
        services.AddApiPipelineCorrelationId(configuration);
        services.AddApiPipelineSecurityHeaders(configuration);
        services.AddApiPipelineRequestSizeTracking();
        _services = services.BuildServiceProvider();

        static Task Terminal(HttpContext _) => Task.CompletedTask;

        _empty = Terminal;

        // Constructed the way UseMiddleware constructs them — once, with the next delegate baked
        // in. All three are convention-based, so there is no per-request factory or DI resolution
        // to measure; what these numbers show is the middleware body and nothing else.
        var security = new SecurityHeadersMiddleware(
            Terminal,
            _services.GetRequiredService<IOptions<ApiPipelineSecurityHeadersOptions>>());
        _securityHeaders = security.InvokeAsync;

        var correlation = new CorrelationIdMiddleware(
            Terminal,
            _services.GetRequiredService<ILogger<CorrelationIdMiddleware>>(),
            _services.GetRequiredService<IOptions<ApiPipelineCorrelationIdOptions>>(),
            _services.GetRequiredService<ApiPipelineMetrics>());
        _correlationId = correlation.InvokeAsync;

        var size = new RequestSizeMiddleware(Terminal, _services.GetRequiredService<ApiPipelineMetrics>());
        _requestSize = size.InvokeAsync;

        var composed = new CorrelationIdMiddleware(
            security.InvokeAsync,
            _services.GetRequiredService<ILogger<CorrelationIdMiddleware>>(),
            _services.GetRequiredService<IOptions<ApiPipelineCorrelationIdOptions>>(),
            _services.GetRequiredService<ApiPipelineMetrics>());
        _fullPipeline = composed.InvokeAsync;

        // A terminal that suspends, like a real endpoint. Only then does `await _next` allocate a
        // state machine, so only these scenarios show what returning the task directly saves.
        static async Task SuspendingTerminal(HttpContext _) => await Task.Yield();

        _emptySuspending = SuspendingTerminal;
        _securityHeadersSuspending = new SecurityHeadersMiddleware(
            SuspendingTerminal,
            _services.GetRequiredService<IOptions<ApiPipelineSecurityHeadersOptions>>()).InvokeAsync;
        _requestSizeSuspending = new RequestSizeMiddleware(SuspendingTerminal, _services.GetRequiredService<ApiPipelineMetrics>()).InvokeAsync;
    }

    private static DefaultHttpContext NewContext(bool withCorrelationHeader, long? contentLength)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Path = "/orders/42";
        context.Request.Scheme = "https";
        if (withCorrelationHeader)
        {
            context.Request.Headers["X-Correlation-Id"] = "0123456789abcdef0123456789abcdef";
        }
        if (contentLength is { } length)
        {
            context.Request.ContentLength = length;
        }
        return context;
    }

    /// <summary>Baseline: the cost of the harness itself.</summary>
    [Benchmark(Baseline = true)]
    public Task EmptyPipeline() => _empty(NewContext(false, null));

    /// <summary>Correlation id when the caller supplied a well-formed id (the validation path).</summary>
    [Benchmark]
    public Task CorrelationId_InboundIdAccepted() => _correlationId(NewContext(true, null));

    /// <summary>Correlation id when the server must generate one.</summary>
    [Benchmark]
    public Task CorrelationId_IdGenerated() => _correlationId(NewContext(false, null));

    /// <summary>Security headers, including the OnStarting registration.</summary>
    [Benchmark]
    public Task SecurityHeaders() => _securityHeaders(NewContext(false, null));

    /// <summary>Request size telemetry with a Content-Length present.</summary>
    [Benchmark]
    public Task RequestSizeTracking() => _requestSize(NewContext(false, 2048));

    /// <summary>Correlation id and security headers composed, as the pipeline orders them.</summary>
    [Benchmark]
    public Task CorrelationIdAndSecurityHeaders() => _fullPipeline(NewContext(true, null));

    /// <summary>
    /// Baseline for the suspending scenarios: the cost of a terminal delegate that yields, which is
    /// what every real endpoint does.
    /// </summary>
    [Benchmark]
    public Task EmptyPipeline_SuspendingTerminal() => _emptySuspending(NewContext(false, null));

    /// <summary>
    /// Security headers over a suspending continuation; compare with
    /// <see cref="EmptyPipeline_SuspendingTerminal"/> for the middleware's real per-request cost.
    /// </summary>
    [Benchmark]
    public Task SecurityHeaders_SuspendingTerminal() => _securityHeadersSuspending(NewContext(false, null));

    /// <summary>Request size telemetry over a suspending continuation.</summary>
    [Benchmark]
    public Task RequestSizeTracking_SuspendingTerminal() => _requestSizeSuspending(NewContext(false, 2048));

    private sealed class BenchmarkEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";
        public string ApplicationName { get; set; } = "perf";
        public string ContentRootPath { get; set; } = "/";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}

/// <summary>
/// Cost of resolving a rate limit partition, which runs on every request when rate limiting is on.
/// </summary>
[MemoryDiagnoser]
public class RateLimitPartitionBenchmarks
{
    private ApiPipelineRateLimitPolicy _policy = null!;

    /// <summary>Builds the policy under test.</summary>
    [GlobalSetup]
    public void Setup() => _policy = new ApiPipelineRateLimitPolicy
    {
        Name = "standard",
        Kind = RateLimiterKind.FixedWindow,
        PermitLimit = 100,
        Window = TimeSpan.FromSeconds(60),
        QueueLimit = 0,
        QueueProcessingOrder = QueueProcessingOrder.OldestFirst
    };

    private static readonly System.Net.IPAddress Ipv4Client = System.Net.IPAddress.Parse("203.0.113.42");
    private static readonly System.Net.IPAddress Ipv6Client = System.Net.IPAddress.Parse("2001:db8:1:2:3:4:5:6");

    /// <summary>Hashing an API key into a partition key, on the per-request path.</summary>
    [Benchmark]
    public string ApiKeyPartitionKey() =>
        ApiPipeline.NET.RateLimiting.RateLimitPartitionKeys.ForApiKey("sk-live-0123456789abcdef", "strict:0a1b2c3d");

    /// <summary>Building an IPv4 partition key, on the per-request path.</summary>
    [Benchmark]
    public string IpPartitionKeyV4() =>
        ApiPipeline.NET.RateLimiting.RateLimitPartitionKeys.ForIp(Ipv4Client, 64, "strict:0a1b2c3d");

    /// <summary>Building an IPv6 partition key, which additionally masks to the configured prefix.</summary>
    [Benchmark]
    public string IpPartitionKeyV6() =>
        ApiPipeline.NET.RateLimiting.RateLimitPartitionKeys.ForIp(Ipv6Client, 64, "strict:0a1b2c3d");
}

/// <summary>
/// The rate-limited request path end to end: <c>UseRateLimiter</c>, the global limiter, the
/// partition factory, lease acquisition and, when rejected, the shared 429 writer.
/// </summary>
[MemoryDiagnoser]
public class RateLimiterPipelineBenchmarks
{
    private RequestDelegate _rateLimited = null!;
    private ServiceProvider _services = null!;

    /// <summary>Builds a real rate-limiting pipeline over the package's own registration.</summary>
    [GlobalSetup]
    public void Setup()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ApiPipeline:RateLimiting:Enabled"] = "true",
                ["ApiPipeline:RateLimiting:DefaultPolicy"] = "standard",
                ["ApiPipeline:RateLimiting:Policies:0:Name"] = "standard",
                ["ApiPipeline:RateLimiting:Policies:0:Kind"] = "FixedWindow",
                // Effectively unreachable, so every measured request takes the acquire path rather
                // than drifting into rejections part-way through a run.
                ["ApiPipeline:RateLimiting:Policies:0:PermitLimit"] = "1000000000",
                ["ApiPipeline:RateLimiting:Policies:0:Window"] = "01:00:00",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging(b => b.ClearProviders());
        services.AddSingleton<IConfiguration>(configuration);
        services.AddApiPipelineRateLimiting(configuration);
        _services = services.BuildServiceProvider();

        // Hosted services run the package's startup checks, and nothing starts them here — so
        // force them the same way host start would.
        BenchmarkPipeline.StartHostedServices(_services);

        IApplicationBuilder app = new ApplicationBuilder(_services);
        app.UseRateLimiter();
        app.Run(static _ => Task.CompletedTask);
        _rateLimited = app.Build();
    }

    /// <summary>Releases the limiter and its partition table.</summary>
    [GlobalCleanup]
    public void Cleanup() => _services.Dispose();

    private DefaultHttpContext NewContext()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = "/orders/42";
        // The limiter's OnRejected and partition factory both read from the request's services.
        context.RequestServices = _services;
        context.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("203.0.113.42");
        return context;
    }

    /// <summary>A request that acquires a lease: the path every allowed request takes.</summary>
    [Benchmark]
    public Task RateLimiter_LeaseAcquired() => _rateLimited(NewContext());
}

/// <summary>
/// The full pipeline under concurrency, all requests in one partition (a flood from one address),
/// to measure the package's only lock contention: the rate limiter's partition and its limiter.
/// </summary>
[MemoryDiagnoser]
public class ConcurrentPipelineBenchmarks
{
    private RequestDelegate _pipeline = null!;
    private ServiceProvider _services = null!;

    /// <summary>Requests issued concurrently per operation.</summary>
    [Params(1, 8, 64)]
    public int Concurrency { get; set; }

    /// <summary>Builds correlation id, security headers, and rate limiting as one pipeline.</summary>
    [GlobalSetup]
    public void Setup()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ApiPipeline:SecurityHeaders:Enabled"] = "true",
                ["ApiPipeline:RateLimiting:Enabled"] = "true",
                ["ApiPipeline:RateLimiting:DefaultPolicy"] = "standard",
                ["ApiPipeline:RateLimiting:Policies:0:Name"] = "standard",
                ["ApiPipeline:RateLimiting:Policies:0:Kind"] = "FixedWindow",
                ["ApiPipeline:RateLimiting:Policies:0:PermitLimit"] = "1000000000",
                ["ApiPipeline:RateLimiting:Policies:0:Window"] = "01:00:00",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging(b => b.ClearProviders());
        services.AddSingleton<IConfiguration>(configuration);
        services.AddApiPipelineCorrelationId(configuration);
        services.AddApiPipelineSecurityHeaders(configuration);
        services.AddApiPipelineRateLimiting(configuration);
        _services = services.BuildServiceProvider();

        BenchmarkPipeline.StartHostedServices(_services);

        static Task Terminal(HttpContext _) => Task.CompletedTask;

        var security = new SecurityHeadersMiddleware(
            Terminal,
            _services.GetRequiredService<IOptions<ApiPipelineSecurityHeadersOptions>>());

        var correlation = new CorrelationIdMiddleware(
            security.InvokeAsync,
            _services.GetRequiredService<ILogger<CorrelationIdMiddleware>>(),
            _services.GetRequiredService<IOptions<ApiPipelineCorrelationIdOptions>>(),
            _services.GetRequiredService<ApiPipelineMetrics>());

        IApplicationBuilder app = new ApplicationBuilder(_services);
        app.UseRateLimiter();
        app.Run(correlation.InvokeAsync);
        _pipeline = app.Build();
    }

    /// <summary>Releases the limiter and its partition table.</summary>
    [GlobalCleanup]
    public void Cleanup() => _services.Dispose();

    /// <summary><see cref="Concurrency"/> requests through the pipeline at once, all one partition.</summary>
    [Benchmark]
    public async Task FullPipeline_Concurrent()
    {
        var pending = new Task[Concurrency];

        for (var i = 0; i < pending.Length; i++)
        {
            var context = new DefaultHttpContext();
            context.Request.Method = "GET";
            context.Request.Path = "/orders/42";
            context.Request.Scheme = "https";
            context.RequestServices = _services;
            // One address for every request: same partition, so the contention is real.
            context.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("203.0.113.42");
            pending[i] = Task.Run(() => _pipeline(context));
        }

        await Task.WhenAll(pending);
    }

    private sealed class ConcurrentBenchmarkEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";
        public string ApplicationName { get; set; } = "perf";
        public string ContentRootPath { get; set; } = "/";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
