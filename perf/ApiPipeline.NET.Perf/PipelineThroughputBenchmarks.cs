using ApiPipeline.NET.Extensions;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using Microsoft.AspNetCore.TestHost;

namespace ApiPipeline.NET.Perf;

/// <summary>
/// Compares minimal vs full ApiPipeline request handling cost.
/// </summary>
[MemoryDiagnoser]
public class PipelineThroughputBenchmarks
{
    private WebApplication _minimalApp = null!;
    private HttpClient _minimalClient = null!;
    private WebApplication _fullApp = null!;
    private HttpClient _fullClient = null!;

    /// <summary>Builds and starts benchmark applications.</summary>
    [GlobalSetup]
    public async Task Setup()
    {
        _minimalApp = await BuildAppAsync(fullPipeline: false);
        _minimalClient = _minimalApp.GetTestClient();

        _fullApp = await BuildAppAsync(fullPipeline: true);
        _fullClient = _fullApp.GetTestClient();
    }

    /// <summary>Disposes benchmark applications.</summary>
    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _minimalApp.DisposeAsync();
        await _fullApp.DisposeAsync();
    }

    /// <summary>Baseline request cost with minimal middleware.</summary>
    [Benchmark(Baseline = true)]
    public Task<HttpResponseMessage> MinimalPipeline_GetPing() => _minimalClient.GetAsync("/ping");

    /// <summary>Request cost with full ApiPipeline middleware.</summary>
    [Benchmark]
    public Task<HttpResponseMessage> FullPipeline_GetPing() => _fullClient.GetAsync("/ping");

    private static async Task<WebApplication> BuildAppAsync(bool fullPipeline)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();

        var config = new Dictionary<string, string?>
        {
            ["RateLimitingOptions:Enabled"] = fullPipeline ? "true" : "false",
            ["RateLimitingOptions:AnonymousFallback"] = "RateLimit",
            ["RateLimitingOptions:DefaultPolicy"] = "strict",
            ["RateLimitingOptions:Policies:0:Name"] = "strict",
            ["RateLimitingOptions:Policies:0:Kind"] = "FixedWindow",
            ["RateLimitingOptions:Policies:0:PermitLimit"] = "100000",
            ["RateLimitingOptions:Policies:0:WindowSeconds"] = "60",
            ["RateLimitingOptions:Policies:0:QueueLimit"] = "0",
            ["RateLimitingOptions:Policies:0:QueueProcessingOrder"] = "OldestFirst",
            ["ResponseCompressionOptions:Enabled"] = fullPipeline ? "true" : "false",
            ["ResponseCachingOptions:Enabled"] = fullPipeline ? "true" : "false",
            ["SecurityHeadersOptions:Enabled"] = fullPipeline ? "true" : "false",
            ["CorsOptions:Enabled"] = fullPipeline ? "true" : "false",
            ["CorsOptions:AllowAllInDevelopment"] = "true",
            ["ApiVersionDeprecationOptions:Enabled"] = "false",
            ["RequestLimitsOptions:Enabled"] = fullPipeline ? "true" : "false",
            ["ForwardedHeadersOptions:Enabled"] = "false"
        };
        builder.Configuration.AddInMemoryCollection(config);

        builder.Services.AddRouting();
        builder.Services.AddApiPipeline(builder.Configuration);
        var app = builder.Build();

        if (fullPipeline)
        {
            app.UseApiPipeline(p => p
                .WithCorrelationId()
                .WithRateLimiting()
                .WithResponseCompression()
                .WithResponseCaching()
                .WithSecurityHeaders()
                .WithCors());
        }

        app.MapGet("/ping", () => Results.Ok("pong"));
        await app.StartAsync();
        return app;
    }
}
