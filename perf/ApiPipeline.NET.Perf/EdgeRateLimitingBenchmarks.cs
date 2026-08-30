using System.Globalization;
using System.Net;
using ApiPipeline.NET.Pipeline;
using BenchmarkDotNet.Attributes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ApiPipeline.NET.Perf;

internal static class EdgeBenchmarkPipeline
{
    public static ServiceProvider BuildProvider(int permitLimit)
    {
        var configuration = new ConfigurationBuilder().Build();
        return BenchmarkPipeline.BuildProvider(configuration, (services, _) =>
            services.AddApiPipelineEdgeRateLimiting(options =>
            {
                options.Enabled = true;
                options.PermitLimit = permitLimit;
                options.Window = TimeSpan.FromHours(1);
                options.MaxIpPartitions = 10_000;
            }));
    }

    public static RequestDelegate BuildBaseline(IServiceProvider services, RequestDelegate terminal) =>
        BenchmarkPipeline.BuildDelegate(services, static app => app.UseRouting(), terminal);

    public static RequestDelegate BuildLimiter(IServiceProvider services, RequestDelegate terminal) =>
        BenchmarkPipeline.BuildDelegate(
            services,
            static app => app.UseApiPipeline(pipeline => pipeline.WithEdgeRateLimiting()),
            terminal);

    public static DefaultHttpContext CreateContext(IServiceProvider services, IPAddress address)
    {
        var context = BenchmarkPipeline.CreateContext(services, HttpMethods.Get, "/edge", "https");
        context.Connection.RemoteIpAddress = address;
        return context;
    }
}

[MemoryDiagnoser]
public class EdgeRateLimitingAllowedBenchmarks
{
    private const int InvocationsPerIteration = 64;
    private const int PermitLimit = 128;

    private ServiceProvider _services = null!;
    private RequestDelegate _baseline = null!;
    private RequestDelegate _limiter = null!;
    private IPAddress _iterationAddress = null!;
    private int _partitionSequence;
    private int _baselineBenchmarkCalls;
    private int _baselineTerminalCalls;
    private int _limiterBenchmarkCalls;
    private int _limiterTerminalCalls;
    private int _unexpectedStatuses;

    [GlobalSetup]
    public async Task Setup()
    {
        _services = EdgeBenchmarkPipeline.BuildProvider(PermitLimit);
        _baseline = EdgeBenchmarkPipeline.BuildBaseline(_services, context =>
        {
            Interlocked.Increment(ref _baselineTerminalCalls);
            context.Response.StatusCode = StatusCodes.Status200OK;
            return Task.CompletedTask;
        });
        _limiter = EdgeBenchmarkPipeline.BuildLimiter(_services, context =>
        {
            Interlocked.Increment(ref _limiterTerminalCalls);
            context.Response.StatusCode = StatusCodes.Status200OK;
            return Task.CompletedTask;
        });
        BenchmarkPipeline.StartHostedServices(_services);

        var setupContext = EdgeBenchmarkPipeline.CreateContext(
            _services, IPAddress.Parse("2001:db8:ffff::1"));
        await _limiter(setupContext);
        BenchmarkPipeline.Require(setupContext.Response.StatusCode == StatusCodes.Status200OK,
            "edge limiter setup request was not allowed");
        BenchmarkPipeline.Require(_limiterTerminalCalls == 1,
            "edge limiter setup request did not invoke the terminal");
        _limiterTerminalCalls = 0;
    }

    [IterationSetup]
    public void IterationSetup()
    {
        var network = ++_partitionSequence;
        _iterationAddress = IPAddress.Parse(string.Create(
            CultureInfo.InvariantCulture,
            $"2001:db8:{network:x}::1"));
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        BenchmarkPipeline.Require(_baselineTerminalCalls == _baselineBenchmarkCalls,
            "allowed baseline terminal count drifted from measured invocations");
        BenchmarkPipeline.Require(_limiterTerminalCalls == _limiterBenchmarkCalls,
            "an allowed edge request did not reach the terminal");
        BenchmarkPipeline.Require(_unexpectedStatuses == 0,
            "an allowed edge request returned a status code of 400 or greater");
        _services.Dispose();
    }

    [Benchmark(Baseline = true), InvocationCount(InvocationsPerIteration)]
    public async Task Baseline_AllowedRequest()
    {
        _baselineBenchmarkCalls++;
        var context = EdgeBenchmarkPipeline.CreateContext(_services, _iterationAddress);
        await _baseline(context);
        if (context.Response.StatusCode >= StatusCodes.Status400BadRequest)
        {
            _unexpectedStatuses++;
        }
    }

    [Benchmark, InvocationCount(InvocationsPerIteration)]
    public async Task EdgeRateLimiter_Allowed()
    {
        _limiterBenchmarkCalls++;
        var context = EdgeBenchmarkPipeline.CreateContext(_services, _iterationAddress);
        await _limiter(context);
        if (context.Response.StatusCode >= StatusCodes.Status400BadRequest)
        {
            _unexpectedStatuses++;
        }
    }
}

[MemoryDiagnoser]
public class EdgeRateLimitingRejectedBenchmarks
{
    private static readonly IPAddress ExhaustedAddress = IPAddress.Parse("203.0.113.90");

    private ServiceProvider _services = null!;
    private RequestDelegate _baseline = null!;
    private RequestDelegate _limiter = null!;
    private int _baselineBenchmarkCalls;
    private int _baselineTerminalCalls;
    private int _limiterBenchmarkCalls;
    private int _limiterTerminalCalls;
    private int _unexpectedStatuses;

    [GlobalSetup]
    public async Task Setup()
    {
        _services = EdgeBenchmarkPipeline.BuildProvider(permitLimit: 1);
        _baseline = EdgeBenchmarkPipeline.BuildBaseline(_services, context =>
        {
            Interlocked.Increment(ref _baselineTerminalCalls);
            context.Response.StatusCode = StatusCodes.Status200OK;
            return Task.CompletedTask;
        });
        _limiter = EdgeBenchmarkPipeline.BuildLimiter(_services, context =>
        {
            Interlocked.Increment(ref _limiterTerminalCalls);
            context.Response.StatusCode = StatusCodes.Status200OK;
            return Task.CompletedTask;
        });
        BenchmarkPipeline.StartHostedServices(_services);

        var allowed = EdgeBenchmarkPipeline.CreateContext(_services, ExhaustedAddress);
        await _limiter(allowed);
        BenchmarkPipeline.Require(allowed.Response.StatusCode == StatusCodes.Status200OK,
            "the rejection partition could not be spent during setup");

        var rejected = EdgeBenchmarkPipeline.CreateContext(_services, ExhaustedAddress);
        await _limiter(rejected);
        BenchmarkPipeline.Require(rejected.Response.StatusCode == StatusCodes.Status429TooManyRequests,
            "the exhausted edge partition did not return 429 during setup");
        BenchmarkPipeline.Require(_limiterTerminalCalls == 1,
            "the edge rejection invoked the terminal during setup");

        _baselineTerminalCalls = 0;
        _limiterTerminalCalls = 0;
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        BenchmarkPipeline.Require(_baselineTerminalCalls == _baselineBenchmarkCalls,
            "rejection baseline terminal count drifted from measured invocations");
        BenchmarkPipeline.Require(_limiterTerminalCalls == 0,
            "an exhausted edge request unexpectedly reached the terminal");
        BenchmarkPipeline.Require(_baselineBenchmarkCalls + _limiterBenchmarkCalls > 0,
            "edge rejection benchmark process did not exercise a measured path");
        BenchmarkPipeline.Require(_unexpectedStatuses == 0,
            "a rejection path returned an unexpected status code");
        _services.Dispose();
    }

    [Benchmark(Baseline = true)]
    public async Task Baseline_UnlimitedRequest()
    {
        _baselineBenchmarkCalls++;
        var context = EdgeBenchmarkPipeline.CreateContext(_services, ExhaustedAddress);
        await _baseline(context);
        if (context.Response.StatusCode != StatusCodes.Status200OK)
        {
            _unexpectedStatuses++;
        }
    }

    [Benchmark]
    public async Task EdgeRateLimiter_Rejected()
    {
        _limiterBenchmarkCalls++;
        var context = EdgeBenchmarkPipeline.CreateContext(_services, ExhaustedAddress);
        await _limiter(context);
        if (context.Response.StatusCode != StatusCodes.Status429TooManyRequests)
        {
            _unexpectedStatuses++;
        }
    }
}
