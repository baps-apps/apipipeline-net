using ApiPipeline.NET.Pipeline;
using BenchmarkDotNet.Attributes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ApiPipeline.NET.Perf;

internal static class CacheBenchmarkPipeline
{
    private static readonly Endpoint OutputCacheEndpoint = new(
        static _ => Task.CompletedTask,
        new EndpointMetadataCollection(new OutputCacheAttribute()),
        "benchmark-output-cache");

    public static ServiceProvider BuildResponseProvider()
    {
        var configuration = new ConfigurationBuilder().Build();
        return BenchmarkPipeline.BuildProvider(configuration, static (services, _) =>
            services.AddApiPipelineResponseCaching(options => options.Enabled = true));
    }

    public static ServiceProvider BuildOutputProvider(IOutputCacheStore? store = null)
    {
        var configuration = new ConfigurationBuilder().Build();
        return BenchmarkPipeline.BuildProvider(configuration, (services, _) =>
        {
            services.AddApiPipelineOutputCaching(options => options.Enabled = true);
            if (store is not null)
            {
                services.Replace(ServiceDescriptor.Singleton(store));
            }
        });
    }

    public static RequestDelegate BuildBaseline(IServiceProvider services, RequestDelegate terminal) =>
        BenchmarkPipeline.BuildDelegate(services, static app => app.UseRouting(), terminal);

    public static RequestDelegate BuildResponseCache(IServiceProvider services, RequestDelegate terminal) =>
        BenchmarkPipeline.BuildDelegate(
            services,
            static app => app.UseApiPipeline(pipeline => pipeline.WithResponseCaching()),
            terminal);

    public static RequestDelegate BuildOutputCache(IServiceProvider services, RequestDelegate terminal) =>
        BenchmarkPipeline.BuildDelegate(
            services,
            static app => app.UseApiPipeline(pipeline => pipeline.WithOutputCaching()),
            terminal);

    public static DefaultHttpContext CreateContext(
        IServiceProvider services,
        PathString path,
        CountingWriteStream sink,
        bool outputCache)
    {
        sink.Reset();
        var context = BenchmarkPipeline.CreateContext(services, HttpMethods.Get, path, "https");
        context.Response.Body = sink;
        if (outputCache)
        {
            context.SetEndpoint(OutputCacheEndpoint);
        }

        return context;
    }

    public static Task WriteCacheableAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "text/plain; charset=utf-8";
        context.Response.Headers.CacheControl = "public, max-age=86400";
        return context.Response.WriteAsync("cache benchmark payload");
    }
}

[MemoryDiagnoser]
public class ResponseCachingHitBenchmarks
{
    private static readonly PathString HitPath = "/response-cache/hit";
    private readonly CountingWriteStream _baselineSink = new();
    private readonly CountingWriteStream _cacheSink = new();
    private ServiceProvider _services = null!;
    private RequestDelegate _baseline = null!;
    private RequestDelegate _cache = null!;
    private int _cacheTerminalCalls;
    private int _expectedCacheTerminalCalls;

    [GlobalSetup]
    public async Task Setup()
    {
        _services = CacheBenchmarkPipeline.BuildResponseProvider();
        _baseline = CacheBenchmarkPipeline.BuildBaseline(_services, CacheBenchmarkPipeline.WriteCacheableAsync);
        _cache = CacheBenchmarkPipeline.BuildResponseCache(_services, async context =>
        {
            Interlocked.Increment(ref _cacheTerminalCalls);
            await CacheBenchmarkPipeline.WriteCacheableAsync(context);
        });
        BenchmarkPipeline.StartHostedServices(_services);

        await _cache(CreateCacheContext());
        BenchmarkPipeline.Require(_cacheTerminalCalls == 1,
            "response-cache population did not invoke the terminal exactly once");
        await _cache(CreateCacheContext());
        BenchmarkPipeline.Require(_cacheTerminalCalls == 1,
            "response-cache hit invoked the terminal instead of serving the stored response");
        _expectedCacheTerminalCalls = _cacheTerminalCalls;
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        BenchmarkPipeline.Require(_cacheTerminalCalls == _expectedCacheTerminalCalls,
            "the one-day response-cache hit entry expired or bypassed during measurement");
        _services.Dispose();
    }

    [Benchmark(Baseline = true)]
    public Task Baseline_HitHarness() =>
        _baseline(CacheBenchmarkPipeline.CreateContext(_services, HitPath, _baselineSink, outputCache: false));

    [Benchmark]
    public Task Cache_Hit() => _cache(CreateCacheContext());

    private DefaultHttpContext CreateCacheContext() =>
        CacheBenchmarkPipeline.CreateContext(_services, HitPath, _cacheSink, outputCache: false);
}

[MemoryDiagnoser]
public class ResponseCachingMissBenchmarks
{
    private readonly CountingWriteStream _baselineSink = new();
    private readonly CountingWriteStream _cacheSink = new();
    private ServiceProvider _services = null!;
    private RequestDelegate _baseline = null!;
    private RequestDelegate _cache = null!;
    private PathString _path;
    private int _pathSequence;
    private int _baselineBenchmarkCalls;
    private int _baselineTerminalCalls;
    private int _cacheBenchmarkCalls;
    private int _cacheTerminalCalls;

    [GlobalSetup]
    public async Task Setup()
    {
        _services = CacheBenchmarkPipeline.BuildResponseProvider();
        _baseline = CacheBenchmarkPipeline.BuildBaseline(_services, async context =>
        {
            Interlocked.Increment(ref _baselineTerminalCalls);
            await CacheBenchmarkPipeline.WriteCacheableAsync(context);
        });
        _cache = CacheBenchmarkPipeline.BuildResponseCache(_services, async context =>
        {
            Interlocked.Increment(ref _cacheTerminalCalls);
            await CacheBenchmarkPipeline.WriteCacheableAsync(context);
        });
        BenchmarkPipeline.StartHostedServices(_services);

        _path = "/response-cache/setup";
        await _cache(CreateCacheContext());
        await _cache(CreateCacheContext());
        BenchmarkPipeline.Require(_cacheTerminalCalls == 1,
            "response-cache setup did not prove a stored response can be hit");
        _baselineTerminalCalls = 0;
        _cacheTerminalCalls = 0;
    }

    [IterationSetup]
    public void IterationSetup() =>
        _path = $"/response-cache/miss/{Environment.ProcessId}/{++_pathSequence}";

    [GlobalCleanup]
    public void Cleanup()
    {
        BenchmarkPipeline.Require(_baselineTerminalCalls == _baselineBenchmarkCalls,
            "response-cache miss baseline terminal count drifted from measured invocations");
        BenchmarkPipeline.Require(_cacheTerminalCalls == _cacheBenchmarkCalls,
            "a response-cache miss unexpectedly hit or bypassed storage work");
        _services.Dispose();
    }

    [Benchmark(Baseline = true), InvocationCount(1)]
    public Task Baseline_MissHarness()
    {
        _baselineBenchmarkCalls++;
        return _baseline(CacheBenchmarkPipeline.CreateContext(
            _services, _path, _baselineSink, outputCache: false));
    }

    [Benchmark, InvocationCount(1)]
    public Task Cache_Miss()
    {
        _cacheBenchmarkCalls++;
        return _cache(CreateCacheContext());
    }

    private DefaultHttpContext CreateCacheContext() =>
        CacheBenchmarkPipeline.CreateContext(_services, _path, _cacheSink, outputCache: false);
}

[MemoryDiagnoser]
public class OutputCachingHitBenchmarks
{
    private static readonly PathString HitPath = "/output-cache/hit";
    private readonly CountingWriteStream _baselineSink = new();
    private readonly CountingWriteStream _cacheSink = new();
    private ServiceProvider _services = null!;
    private RequestDelegate _baseline = null!;
    private RequestDelegate _cache = null!;
    private int _cacheTerminalCalls;
    private int _expectedCacheTerminalCalls;

    [GlobalSetup]
    public async Task Setup()
    {
        _services = CacheBenchmarkPipeline.BuildOutputProvider();
        _baseline = CacheBenchmarkPipeline.BuildBaseline(_services, CacheBenchmarkPipeline.WriteCacheableAsync);
        _cache = CacheBenchmarkPipeline.BuildOutputCache(_services, async context =>
        {
            Interlocked.Increment(ref _cacheTerminalCalls);
            await CacheBenchmarkPipeline.WriteCacheableAsync(context);
        });
        BenchmarkPipeline.StartHostedServices(_services);

        await _cache(CreateCacheContext());
        BenchmarkPipeline.Require(_cacheTerminalCalls == 1,
            "output-cache population did not invoke the terminal exactly once");
        await _cache(CreateCacheContext());
        BenchmarkPipeline.Require(_cacheTerminalCalls == 1,
            "output-cache hit invoked the terminal instead of serving the stored response");
        _expectedCacheTerminalCalls = _cacheTerminalCalls;
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        BenchmarkPipeline.Require(_cacheTerminalCalls == _expectedCacheTerminalCalls,
            "the one-day output-cache hit entry expired or bypassed during measurement");
        _services.Dispose();
    }

    [Benchmark(Baseline = true)]
    public Task Baseline_HitHarness() =>
        _baseline(CacheBenchmarkPipeline.CreateContext(_services, HitPath, _baselineSink, outputCache: true));

    [Benchmark]
    public Task Cache_Hit() => _cache(CreateCacheContext());

    private DefaultHttpContext CreateCacheContext() =>
        CacheBenchmarkPipeline.CreateContext(_services, HitPath, _cacheSink, outputCache: true);
}

[MemoryDiagnoser]
public class OutputCachingMissBenchmarks
{
    private readonly CountingWriteStream _baselineSink = new();
    private readonly CountingWriteStream _cacheSink = new();
    private ServiceProvider _services = null!;
    private RequestDelegate _baseline = null!;
    private RequestDelegate _cache = null!;
    private PathString _path;
    private int _pathSequence;
    private int _baselineBenchmarkCalls;
    private int _baselineTerminalCalls;
    private int _cacheBenchmarkCalls;
    private int _cacheTerminalCalls;

    [GlobalSetup]
    public async Task Setup()
    {
        _services = CacheBenchmarkPipeline.BuildOutputProvider();
        _baseline = CacheBenchmarkPipeline.BuildBaseline(_services, async context =>
        {
            Interlocked.Increment(ref _baselineTerminalCalls);
            await CacheBenchmarkPipeline.WriteCacheableAsync(context);
        });
        _cache = CacheBenchmarkPipeline.BuildOutputCache(_services, async context =>
        {
            Interlocked.Increment(ref _cacheTerminalCalls);
            await CacheBenchmarkPipeline.WriteCacheableAsync(context);
        });
        BenchmarkPipeline.StartHostedServices(_services);

        _path = "/output-cache/setup";
        await _cache(CreateCacheContext());
        await _cache(CreateCacheContext());
        BenchmarkPipeline.Require(_cacheTerminalCalls == 1,
            "output-cache setup did not prove a stored response can be hit");
        _baselineTerminalCalls = 0;
        _cacheTerminalCalls = 0;
    }

    [IterationSetup]
    public void IterationSetup() =>
        _path = $"/output-cache/miss/{Environment.ProcessId}/{++_pathSequence}";

    [GlobalCleanup]
    public void Cleanup()
    {
        BenchmarkPipeline.Require(_baselineTerminalCalls == _baselineBenchmarkCalls,
            "output-cache miss baseline terminal count drifted from measured invocations");
        BenchmarkPipeline.Require(_cacheTerminalCalls == _cacheBenchmarkCalls,
            "an output-cache miss unexpectedly hit or bypassed storage work");
        _services.Dispose();
    }

    [Benchmark(Baseline = true), InvocationCount(1)]
    public Task Baseline_MissHarness()
    {
        _baselineBenchmarkCalls++;
        return _baseline(CacheBenchmarkPipeline.CreateContext(
            _services, _path, _baselineSink, outputCache: true));
    }

    [Benchmark, InvocationCount(1)]
    public Task Cache_Miss()
    {
        _cacheBenchmarkCalls++;
        return _cache(CreateCacheContext());
    }

    private DefaultHttpContext CreateCacheContext() =>
        CacheBenchmarkPipeline.CreateContext(_services, _path, _cacheSink, outputCache: true);
}
