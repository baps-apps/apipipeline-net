using BenchmarkDotNet.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.DependencyInjection;

namespace ApiPipeline.NET.Perf;

internal sealed class CountingOutputCacheStore : IOutputCacheStore
{
    private int _getCalls;
    private int _setCalls;
    private int _evictCalls;
    private int _lastStoredLength;

    public int GetCalls => Volatile.Read(ref _getCalls);
    public int SetCalls => Volatile.Read(ref _setCalls);
    public int EvictCalls => Volatile.Read(ref _evictCalls);
    public int LastStoredLength => Volatile.Read(ref _lastStoredLength);

    public ValueTask<byte[]?> GetAsync(string key, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _getCalls);
        return ValueTask.FromResult<byte[]?>(null);
    }

    public ValueTask SetAsync(
        string key,
        byte[] value,
        string[]? tags,
        TimeSpan validFor,
        CancellationToken cancellationToken)
    {
        Volatile.Write(ref _lastStoredLength, value.Length);
        Interlocked.Increment(ref _setCalls);
        return ValueTask.CompletedTask;
    }

    public ValueTask EvictByTagAsync(string tag, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _evictCalls);
        return ValueTask.CompletedTask;
    }

    public void ResetCounts()
    {
        Volatile.Write(ref _getCalls, 0);
        Volatile.Write(ref _setCalls, 0);
        Volatile.Write(ref _evictCalls, 0);
        Volatile.Write(ref _lastStoredLength, 0);
    }
}

[MemoryDiagnoser]
public class OutputCachingAllocationBenchmarks
{
    private readonly CountingWriteStream _nonCacheableSink = new();
    private readonly CountingWriteStream _instrumentedSink = new();
    private readonly CountingWriteStream _defaultSink = new();
    private readonly CountingOutputCacheStore _nonCacheableStore = new();
    private readonly CountingOutputCacheStore _countingStore = new();
    private ServiceProvider _nonCacheableServices = null!;
    private ServiceProvider _instrumentedServices = null!;
    private ServiceProvider _defaultServices = null!;
    private RequestDelegate _nonCacheable = null!;
    private RequestDelegate _instrumentedStore = null!;
    private RequestDelegate _defaultStore = null!;
    private byte[] _payload = null!;
    private PathString _nonCacheablePath;
    private PathString _instrumentedPath;
    private PathString _defaultPath;
    private int _pathSequence;
    private int _nonCacheableBenchmarkCalls;
    private int _instrumentedBenchmarkCalls;
    private int _defaultBenchmarkCalls;
    private int _nonCacheableTerminalCalls = 0;
    private int _instrumentedTerminalCalls = 0;
    private int _defaultTerminalCalls = 0;

    [Params(23, 1_024, 65_536)]
    public int PayloadSize { get; set; }

    [GlobalSetup]
    public async Task Setup()
    {
        BuildDiagnosticPipelines();

        var setupPath = $"/output-cache/allocation/setup/{PayloadSize}";
        _nonCacheablePath = setupPath;
        _instrumentedPath = setupPath;
        _defaultPath = setupPath;

        await _nonCacheable(CreateNonCacheableContext());
        BenchmarkPipeline.Require(_nonCacheableTerminalCalls == 1,
            "non-cacheable response did not invoke the terminal exactly once");
        BenchmarkPipeline.Require(_nonCacheableStore.SetCalls == 0,
            "non-cacheable response unexpectedly wrote to the output-cache store");

        await _instrumentedStore(CreateInstrumentedContext());
        BenchmarkPipeline.Require(_instrumentedTerminalCalls == 1,
            "instrumented output-cache miss did not invoke the terminal exactly once");
        BenchmarkPipeline.Require(_countingStore.SetCalls == 1,
            "instrumented output-cache miss did not perform exactly one SetAsync");
        BenchmarkPipeline.Require(_countingStore.LastStoredLength >= PayloadSize,
            "instrumented output-cache store observed fewer bytes than the response payload");

        await _defaultStore(CreateDefaultContext());
        await _defaultStore(CreateDefaultContext());
        BenchmarkPipeline.Require(_defaultTerminalCalls == 1,
            "default output-cache store did not produce a subsequent hit");

        _nonCacheableTerminalCalls = 0;
        _instrumentedTerminalCalls = 0;
        _defaultTerminalCalls = 0;
        _nonCacheableStore.ResetCounts();
        _countingStore.ResetCounts();
    }

    [IterationSetup]
    public void IterationSetup()
    {
        var sequence = ++_pathSequence;
        _nonCacheablePath = $"/output-cache/allocation/non-cacheable/{Environment.ProcessId}/{sequence}";
        _instrumentedPath = $"/output-cache/allocation/instrumented/{Environment.ProcessId}/{sequence}";
        _defaultPath = $"/output-cache/allocation/default/{Environment.ProcessId}/{sequence}";
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        BenchmarkPipeline.Require(_nonCacheableTerminalCalls == _nonCacheableBenchmarkCalls,
            "non-cacheable terminal count drifted from measured invocations");
        BenchmarkPipeline.Require(_instrumentedTerminalCalls == _instrumentedBenchmarkCalls,
            "instrumented-store terminal count drifted from measured invocations");
        BenchmarkPipeline.Require(_defaultTerminalCalls == _defaultBenchmarkCalls,
            "default-store miss unexpectedly hit or bypassed the terminal");
        BenchmarkPipeline.Require(_nonCacheableStore.SetCalls == 0,
            "non-cacheable measurement unexpectedly wrote to the output-cache store");
        BenchmarkPipeline.Require(_countingStore.SetCalls == _instrumentedBenchmarkCalls,
            "instrumented-store SetAsync count drifted from measured invocations");

        _nonCacheableServices.Dispose();
        _instrumentedServices.Dispose();
        _defaultServices.Dispose();
    }

    private void BuildDiagnosticPipelines()
    {
        _payload = new byte[PayloadSize];
        Array.Fill(_payload, (byte)'x');

        _nonCacheableServices = CacheBenchmarkPipeline.BuildOutputProvider(_nonCacheableStore);
        _instrumentedServices = CacheBenchmarkPipeline.BuildOutputProvider(_countingStore);
        _defaultServices = CacheBenchmarkPipeline.BuildOutputProvider();

        _nonCacheable = CacheBenchmarkPipeline.BuildOutputCache(
            _nonCacheableServices,
            async context =>
            {
                Interlocked.Increment(ref _nonCacheableTerminalCalls);
                await WritePayloadAsync(context, cacheable: false);
            });
        _instrumentedStore = CacheBenchmarkPipeline.BuildOutputCache(
            _instrumentedServices,
            async context =>
            {
                Interlocked.Increment(ref _instrumentedTerminalCalls);
                await WritePayloadAsync(context, cacheable: true);
            });
        _defaultStore = CacheBenchmarkPipeline.BuildOutputCache(
            _defaultServices,
            async context =>
            {
                Interlocked.Increment(ref _defaultTerminalCalls);
                await WritePayloadAsync(context, cacheable: true);
            });

        BenchmarkPipeline.StartHostedServices(_nonCacheableServices);
        BenchmarkPipeline.StartHostedServices(_instrumentedServices);
        BenchmarkPipeline.StartHostedServices(_defaultServices);
    }

    private async Task WritePayloadAsync(HttpContext context, bool cacheable)
    {
        if (!cacheable)
        {
            var feature = context.Features.Get<IOutputCacheFeature>()
                ?? throw new InvalidOperationException("The output-cache feature is missing.");
            feature.Context.AllowCacheStorage = false;
        }

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/octet-stream";
        context.Response.Headers.CacheControl = cacheable
            ? "public, max-age=86400"
            : "no-store";
        await context.Response.Body.WriteAsync(_payload);
    }

    private DefaultHttpContext CreateNonCacheableContext() =>
        CacheBenchmarkPipeline.CreateContext(
            _nonCacheableServices, _nonCacheablePath, _nonCacheableSink, outputCache: true);

    private DefaultHttpContext CreateInstrumentedContext() =>
        CacheBenchmarkPipeline.CreateContext(
            _instrumentedServices, _instrumentedPath, _instrumentedSink, outputCache: true);

    private DefaultHttpContext CreateDefaultContext() =>
        CacheBenchmarkPipeline.CreateContext(
            _defaultServices, _defaultPath, _defaultSink, outputCache: true);

    [Benchmark(Baseline = true), InvocationCount(1)]
    public Task NonCacheable()
    {
        _nonCacheableBenchmarkCalls++;
        return _nonCacheable(CreateNonCacheableContext());
    }

    [Benchmark, InvocationCount(1)]
    public Task InstrumentedStoreMiss()
    {
        _instrumentedBenchmarkCalls++;
        return _instrumentedStore(CreateInstrumentedContext());
    }

    [Benchmark, InvocationCount(1)]
    public Task DefaultStoreMiss()
    {
        _defaultBenchmarkCalls++;
        return _defaultStore(CreateDefaultContext());
    }
}
