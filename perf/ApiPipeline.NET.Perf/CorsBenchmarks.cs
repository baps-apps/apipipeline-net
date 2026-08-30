using ApiPipeline.NET.Pipeline;
using BenchmarkDotNet.Attributes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ApiPipeline.NET.Perf;

internal static class CorsBenchmarkPipeline
{
    public const string Origin = "https://allowed.example.com";

    public static ServiceProvider BuildProvider()
    {
        var configuration = new ConfigurationBuilder().Build();
        return BenchmarkPipeline.BuildProvider(configuration, static (services, _) =>
            services.AddApiPipelineCors(options =>
            {
                options.Enabled = true;
                options.AllowedOrigins = [Origin];
            }));
    }

    public static RequestDelegate BuildActive(IServiceProvider services, RequestDelegate terminal) =>
        BenchmarkPipeline.BuildDelegate(
            services,
            static app => app.UseApiPipeline(pipeline => pipeline.WithCors()),
            terminal);

    public static RequestDelegate BuildBaseline(IServiceProvider services, RequestDelegate terminal) =>
        BenchmarkPipeline.BuildDelegate(services, static app => app.UseRouting(), terminal);

    public static DefaultHttpContext CreateGet(IServiceProvider services)
    {
        var context = BenchmarkPipeline.CreateContext(services, HttpMethods.Get, "/cors", "https");
        context.Request.Headers.Origin = Origin;
        return context;
    }

    public static DefaultHttpContext CreatePreflight(IServiceProvider services)
    {
        var context = BenchmarkPipeline.CreateContext(services, HttpMethods.Options, "/cors", "https");
        context.Request.Headers.Origin = Origin;
        context.Request.Headers.AccessControlRequestMethod = HttpMethods.Get;
        return context;
    }
}

[MemoryDiagnoser]
public class CorsGetBenchmarks
{
    private ServiceProvider _services = null!;
    private RequestDelegate _baseline = null!;
    private RequestDelegate _cors = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        _services = CorsBenchmarkPipeline.BuildProvider();
        _baseline = CorsBenchmarkPipeline.BuildBaseline(_services, WriteOkAsync);
        _cors = CorsBenchmarkPipeline.BuildActive(_services, WriteOkAsync);
        BenchmarkPipeline.StartHostedServices(_services);

        var active = CorsBenchmarkPipeline.CreateGet(_services);
        await _cors(active);
        BenchmarkPipeline.Require(
            active.Response.Headers.AccessControlAllowOrigin == CorsBenchmarkPipeline.Origin,
            "CORS GET did not emit Access-Control-Allow-Origin");
    }

    [GlobalCleanup]
    public void Cleanup() => _services.Dispose();

    [Benchmark(Baseline = true)]
    public Task Baseline_RoutedGet() => _baseline(CorsBenchmarkPipeline.CreateGet(_services));

    [Benchmark]
    public Task Cors_AllowedGet() => _cors(CorsBenchmarkPipeline.CreateGet(_services));

    private static Task WriteOkAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status200OK;
        return BenchmarkPipeline.StartResponseAsync(context);
    }
}

[MemoryDiagnoser]
public class CorsPreflightBenchmarks
{
    private ServiceProvider _services = null!;
    private RequestDelegate _baseline = null!;
    private RequestDelegate _cors = null!;
    private int _baselineTerminalCalls;
    private int _corsTerminalCalls;

    [GlobalSetup]
    public async Task Setup()
    {
        _services = CorsBenchmarkPipeline.BuildProvider();
        _baseline = CorsBenchmarkPipeline.BuildBaseline(_services, context =>
        {
            Interlocked.Increment(ref _baselineTerminalCalls);
            context.Response.StatusCode = StatusCodes.Status200OK;
            return Task.CompletedTask;
        });
        _cors = CorsBenchmarkPipeline.BuildActive(_services, context =>
        {
            Interlocked.Increment(ref _corsTerminalCalls);
            context.Response.StatusCode = StatusCodes.Status200OK;
            return Task.CompletedTask;
        });
        BenchmarkPipeline.StartHostedServices(_services);

        var baseline = CorsBenchmarkPipeline.CreatePreflight(_services);
        await _baseline(baseline);
        var active = CorsBenchmarkPipeline.CreatePreflight(_services);
        await _cors(active);

        BenchmarkPipeline.Require(_baselineTerminalCalls == 1,
            "preflight baseline did not invoke its terminal exactly once");
        BenchmarkPipeline.Require(_corsTerminalCalls == 0,
            "CORS preflight did not short-circuit before the terminal");
        BenchmarkPipeline.Require(active.Response.StatusCode == StatusCodes.Status204NoContent,
            "CORS preflight did not return 204");
        BenchmarkPipeline.Require(
            active.Response.Headers.AccessControlAllowOrigin == CorsBenchmarkPipeline.Origin,
            "CORS preflight did not emit Access-Control-Allow-Origin");

        _baselineTerminalCalls = 0;
        _corsTerminalCalls = 0;
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        BenchmarkPipeline.Require(_corsTerminalCalls == 0,
            "a measured CORS preflight unexpectedly invoked the terminal");
        _services.Dispose();
    }

    [Benchmark(Baseline = true)]
    public Task Baseline_OptionsRequest() => _baseline(CorsBenchmarkPipeline.CreatePreflight(_services));

    [Benchmark]
    public Task Cors_PreflightShortCircuit() => _cors(CorsBenchmarkPipeline.CreatePreflight(_services));
}
