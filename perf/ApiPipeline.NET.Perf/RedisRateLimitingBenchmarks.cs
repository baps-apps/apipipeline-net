using System.Globalization;
using System.Net;
using ApiPipeline.NET.Pipeline;
using BenchmarkDotNet.Attributes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace ApiPipeline.NET.Perf;

internal sealed class RedisPipeline : IAsyncDisposable
{
    private readonly ServiceProvider _services;
    private readonly RequestDelegate _pipeline;
    private int _terminalCalls;

    private RedisPipeline(ServiceProvider services, RequestDelegate pipeline)
    {
        _services = services;
        _pipeline = pipeline;
    }

    public int TerminalCalls => Volatile.Read(ref _terminalCalls);

    /// <summary>The pipeline's own services, for scoping a metric listener to its meters.</summary>
    public IServiceProvider Services => _services;

    public static RedisPipeline CreateBaseline()
    {
        var configuration = new ConfigurationBuilder().Build();
        var services = BenchmarkPipeline.BuildProvider(configuration, static (collection, _) =>
            collection.AddRouting());
        RedisPipeline? owner = null;
        var pipeline = BenchmarkPipeline.BuildDelegate(
            services,
            static app => app.UseRouting(),
            context =>
            {
                Interlocked.Increment(ref owner!._terminalCalls);
                context.Response.StatusCode = StatusCodes.Status200OK;
                return Task.CompletedTask;
            });
        owner = new RedisPipeline(services, pipeline);
        return owner;
    }

    public static RedisPipeline Create(
        string connectionString,
        string keyPrefix,
        string defaultKind,
        bool globalAndDefault = false,
        int permitLimit = 1_000_000,
        Action<Dictionary<string, string?>>? configure = null)
    {
        var values = Settings(connectionString, keyPrefix, defaultKind, globalAndDefault, permitLimit);
        configure?.Invoke(values);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = BenchmarkPipeline.BuildProvider(configuration, static (collection, config) =>
        {
            collection.AddApiPipelineRateLimiting(config);
            collection.AddApiPipelineRedisRateLimiting(config);
        });

        RedisPipeline? owner = null;
        var pipeline = BenchmarkPipeline.BuildDelegate(
            services,
            static app => app.UseApiPipeline(builder => builder.WithRateLimiting()),
            context =>
            {
                Interlocked.Increment(ref owner!._terminalCalls);
                context.Response.StatusCode = StatusCodes.Status200OK;
                return Task.CompletedTask;
            });
        owner = new RedisPipeline(services, pipeline);
        BenchmarkPipeline.StartHostedServices(services);
        return owner;
    }

    public static RedisPipeline CreateWithPermitLimit(
        string connectionString,
        string keyPrefix,
        int permitLimit) =>
        Create(connectionString, keyPrefix, "FixedWindow", permitLimit: permitLimit);

    public async Task<int> InvokeAsync(IPAddress address)
    {
        var context = BenchmarkPipeline.CreateContext(_services, HttpMethods.Get, "/redis", "https");
        context.Connection.RemoteIpAddress = address;
        context.SetEndpoint(new Endpoint(
            static _ => Task.CompletedTask,
            EndpointMetadataCollection.Empty,
            "Redis benchmark endpoint"));
        await _pipeline(context);
        return context.Response.StatusCode;
    }

    public ValueTask DisposeAsync() => _services.DisposeAsync();

    private static Dictionary<string, string?> Settings(
        string connectionString,
        string keyPrefix,
        string defaultKind,
        bool globalAndDefault,
        int permitLimit)
    {
        var values = new Dictionary<string, string?>
        {
            ["ApiPipeline:RateLimiting:Enabled"] = "true",
            ["ApiPipeline:RateLimiting:DefaultPolicy"] = "default",
            ["ApiPipeline:RateLimiting:AnonymousFallback"] = "RateLimit",
            ["ApiPipeline:RateLimiting:Redis:Enabled"] = "true",
            ["ApiPipeline:RateLimiting:Redis:Configuration"] = connectionString,
            ["ApiPipeline:RateLimiting:Redis:CommandTimeout"] = "00:00:05",
            ["ApiPipeline:RateLimiting:Redis:KeyPrefix"] = keyPrefix,
            ["ApiPipeline:RateLimiting:Redis:PartitionKeySecret"] =
                "benchmark-partition-secret-32-bytes",
        };

        AddPolicy(values, 0, "default", defaultKind, permitLimit);
        if (globalAndDefault)
        {
            AddPolicy(values, 1, "global", "FixedWindow", permitLimit: 1_000_000);
            values["ApiPipeline:RateLimiting:GlobalPolicy"] = "global";
        }

        return values;
    }

    private static void AddPolicy(
        Dictionary<string, string?> values,
        int index,
        string name,
        string kind,
        int permitLimit)
    {
        var prefix = $"ApiPipeline:RateLimiting:Policies:{index}";
        values[$"{prefix}:Name"] = name;
        values[$"{prefix}:Kind"] = kind;
        values[$"{prefix}:PermitLimit"] = permitLimit.ToString(CultureInfo.InvariantCulture);
        values[$"{prefix}:QueueLimit"] = "0";

        if (!string.Equals(kind, "Concurrency", StringComparison.Ordinal))
        {
            values[$"{prefix}:Window"] = "01:00:00";
        }

        if (string.Equals(kind, "SlidingWindow", StringComparison.Ordinal))
        {
            values[$"{prefix}:SegmentsPerWindow"] = "4";
        }
        else if (string.Equals(kind, "TokenBucket", StringComparison.Ordinal))
        {
            values[$"{prefix}:TokensPerPeriod"] = permitLimit.ToString(CultureInfo.InvariantCulture);
        }
    }
}

internal sealed class RedisCommandStats : IAsyncDisposable
{
    private readonly ConnectionMultiplexer _connection;
    private readonly IServer _server;

    private RedisCommandStats(ConnectionMultiplexer connection)
    {
        _connection = connection;
        _server = connection.GetServer(connection.GetEndPoints()[0]);
    }

    public static async Task<RedisCommandStats> ConnectAsync(string connectionString)
    {
        var connection = await ConnectionMultiplexer.ConnectAsync(connectionString + ",allowAdmin=true");
        return new RedisCommandStats(connection);
    }

    public async Task<long> ScriptCallsAsync()
    {
        var stats = await _server.InfoAsync("commandstats");
        long calls = 0;
        foreach (var (key, value) in stats.SelectMany(static group => group))
        {
            if (key is not ("cmdstat_eval" or "cmdstat_evalsha"))
            {
                continue;
            }

            var field = value.Split(',').First(static item =>
                item.StartsWith("calls=", StringComparison.Ordinal));
            calls += long.Parse(field["calls=".Length..], CultureInfo.InvariantCulture);
        }

        return calls;
    }

    public ValueTask DisposeAsync() => _connection.DisposeAsync();
}

[MemoryDiagnoser]
public class RedisAllowedBenchmarks
{
    private const int InvocationsPerIteration = 16;
    private static readonly IPAddress Address = IPAddress.Parse("198.51.100.70");

    private RedisPipeline _baseline = null!;
    private RedisPipeline _fixedWindow = null!;
    private RedisPipeline _slidingWindow = null!;
    private RedisPipeline _tokenBucket = null!;
    private RedisPipeline _concurrency = null!;
    private RedisPipeline _globalAndDefault = null!;
    private RedisCommandStats _stats = null!;
    private long _scriptCallsBefore;
    private int _baselineTerminalBefore;
    private int _fixedTerminalBefore;
    private int _slidingTerminalBefore;
    private int _tokenTerminalBefore;
    private int _concurrencyTerminalBefore;
    private int _globalTerminalBefore;
    private int _unexpectedStatuses;
    private int _measuredCalls;

    [GlobalSetup]
    public async Task Setup()
    {
        var connection = RedisBenchmarkConnection.Required();
        var run = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        _baseline = RedisPipeline.CreateBaseline();
        _fixedWindow = RedisPipeline.Create(connection, $"perf:{run}:fixed", "FixedWindow");
        _slidingWindow = RedisPipeline.Create(connection, $"perf:{run}:sliding", "SlidingWindow");
        _tokenBucket = RedisPipeline.Create(connection, $"perf:{run}:token", "TokenBucket");
        _concurrency = RedisPipeline.Create(connection, $"perf:{run}:concurrency", "Concurrency");
        _globalAndDefault = RedisPipeline.Create(
            connection, $"perf:{run}:global-default", "FixedWindow", globalAndDefault: true);

        foreach (var pipeline in ActivePipelines())
        {
            BenchmarkPipeline.Require(await pipeline.InvokeAsync(Address) == StatusCodes.Status200OK,
                "a Redis allowed pipeline did not warm successfully");
        }

        _stats = await RedisCommandStats.ConnectAsync(connection);
    }

    [IterationSetup]
    public void IterationSetup() => IterationSetupAsync().GetAwaiter().GetResult();

    private async Task IterationSetupAsync()
    {
        _scriptCallsBefore = await _stats.ScriptCallsAsync();
        _baselineTerminalBefore = _baseline.TerminalCalls;
        _fixedTerminalBefore = _fixedWindow.TerminalCalls;
        _slidingTerminalBefore = _slidingWindow.TerminalCalls;
        _tokenTerminalBefore = _tokenBucket.TerminalCalls;
        _concurrencyTerminalBefore = _concurrency.TerminalCalls;
        _globalTerminalBefore = _globalAndDefault.TerminalCalls;
    }

    [IterationCleanup]
    public void IterationCleanup() => IterationCleanupAsync().GetAwaiter().GetResult();

    private async Task IterationCleanupAsync()
    {
        var baselineCalls = _baseline.TerminalCalls - _baselineTerminalBefore;
        var fixedCalls = _fixedWindow.TerminalCalls - _fixedTerminalBefore;
        var slidingCalls = _slidingWindow.TerminalCalls - _slidingTerminalBefore;
        var tokenCalls = _tokenBucket.TerminalCalls - _tokenTerminalBefore;
        var concurrencyCalls = _concurrency.TerminalCalls - _concurrencyTerminalBefore;
        var globalCalls = _globalAndDefault.TerminalCalls - _globalTerminalBefore;
        var terminalCalls = baselineCalls + fixedCalls + slidingCalls + tokenCalls + concurrencyCalls + globalCalls;
        var exercisedPipelines = new[]
        {
            baselineCalls,
            fixedCalls,
            slidingCalls,
            tokenCalls,
            concurrencyCalls,
            globalCalls
        }.Count(calls => calls > 0);
        BenchmarkPipeline.Require(terminalCalls > 0 && exercisedPipelines == 1,
            $"a Redis allowed iteration exercised {exercisedPipelines} pipelines and reached the terminal {terminalCalls} times");

        var expectedScripts = fixedCalls + slidingCalls + tokenCalls + concurrencyCalls + (2 * globalCalls);
        var scriptCalls = await _stats.ScriptCallsAsync() - _scriptCallsBefore;
        BenchmarkPipeline.Require(scriptCalls == expectedScripts,
            $"a Redis allowed iteration executed {scriptCalls} scripts instead of {expectedScripts}");
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        BenchmarkPipeline.Require(_measuredCalls > 0,
            "the Redis allowed benchmark process did not exercise a measured path");
        BenchmarkPipeline.Require(_unexpectedStatuses == 0,
            "a Redis allowed request returned a non-200 response");

        await _stats.DisposeAsync();
        await _globalAndDefault.DisposeAsync();
        await _concurrency.DisposeAsync();
        await _tokenBucket.DisposeAsync();
        await _slidingWindow.DisposeAsync();
        await _fixedWindow.DisposeAsync();
        await _baseline.DisposeAsync();
    }

    [Benchmark(Baseline = true), InvocationCount(InvocationsPerIteration)]
    public Task Baseline_Request() => InvokeAllowedAsync(_baseline);

    [Benchmark, InvocationCount(InvocationsPerIteration)]
    public Task Redis_FixedWindow() => InvokeAllowedAsync(_fixedWindow);

    [Benchmark, InvocationCount(InvocationsPerIteration)]
    public Task Redis_SlidingWindow() => InvokeAllowedAsync(_slidingWindow);

    [Benchmark, InvocationCount(InvocationsPerIteration)]
    public Task Redis_TokenBucket() => InvokeAllowedAsync(_tokenBucket);

    [Benchmark, InvocationCount(InvocationsPerIteration)]
    public Task Redis_Concurrency() => InvokeAllowedAsync(_concurrency);

    [Benchmark, InvocationCount(InvocationsPerIteration)]
    public Task Redis_GlobalPlusDefault() => InvokeAllowedAsync(_globalAndDefault);

    private IEnumerable<RedisPipeline> ActivePipelines()
    {
        yield return _fixedWindow;
        yield return _slidingWindow;
        yield return _tokenBucket;
        yield return _concurrency;
        yield return _globalAndDefault;
    }

    private async Task InvokeAllowedAsync(RedisPipeline pipeline)
    {
        _measuredCalls++;
        if (await pipeline.InvokeAsync(Address) != StatusCodes.Status200OK)
        {
            _unexpectedStatuses++;
        }
    }
}

[MemoryDiagnoser]
public class RedisServerRejectionBenchmarks
{
    private const int InvocationsPerIteration = 16;

    private RedisPipeline _baseline = null!;
    private RedisPipeline _limiter = null!;
    private RedisCommandStats _stats = null!;
    private IPAddress[] _iterationAddresses = null!;
    private long _scriptCallsBefore;
    private int _partitionSequence;
    private int _iterationAddressIndex;
    private int _baselineCallsAtSetup;
    private int _limiterCallsAtSetup;
    private int _baselineCalls;
    private int _limiterCalls;
    private int _limiterTerminalAtSetup;
    private int _unexpected;

    [GlobalSetup]
    public async Task Setup()
    {
        var connection = RedisBenchmarkConnection.Required();
        var run = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        _baseline = RedisPipeline.CreateBaseline();
        _limiter = RedisPipeline.CreateWithPermitLimit(
            connection, $"perf:{run}:server-reject", permitLimit: 1);
        _stats = await RedisCommandStats.ConnectAsync(connection);

        BenchmarkPipeline.Require(
            await _limiter.InvokeAsync(IPAddress.Parse("2001:db8:20:ffff::1")) == StatusCodes.Status200OK,
            "the Redis rejection pipeline did not warm successfully");
    }

    [IterationSetup]
    public void IterationSetup() => IterationSetupAsync().GetAwaiter().GetResult();

    private async Task IterationSetupAsync()
    {
        _iterationAddresses = new IPAddress[InvocationsPerIteration];
        for (var index = 0; index < _iterationAddresses.Length; index++)
        {
            var address = NextAddress(0x20, ++_partitionSequence);
            _iterationAddresses[index] = address;
            await ExhaustPartitionAsync(address);
        }
        _iterationAddressIndex = 0;
        _scriptCallsBefore = await _stats.ScriptCallsAsync();
        _baselineCallsAtSetup = _baselineCalls;
        _limiterCallsAtSetup = _limiterCalls;
        _limiterTerminalAtSetup = _limiter.TerminalCalls;
    }

    [IterationCleanup]
    public void IterationCleanup() => IterationCleanupAsync().GetAwaiter().GetResult();

    private async Task IterationCleanupAsync()
    {
        var scriptCalls = await _stats.ScriptCallsAsync() - _scriptCallsBefore;
        if (_baselineCalls != _baselineCallsAtSetup)
        {
            BenchmarkPipeline.Require(scriptCalls == 0,
                "the Redis rejection baseline unexpectedly executed a limiter script");
        }
        else if (_limiterCalls != _limiterCallsAtSetup)
        {
            var measuredCalls = _limiterCalls - _limiterCallsAtSetup;
            BenchmarkPipeline.Require(scriptCalls == measuredCalls,
                $"{measuredCalls} server-side Redis rejections executed {scriptCalls} scripts");
            BenchmarkPipeline.Require(_limiter.TerminalCalls == _limiterTerminalAtSetup,
                "a server-side Redis rejection reached the terminal");
        }
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        BenchmarkPipeline.Require(_baselineCalls + _limiterCalls > 0,
            "the server-side Redis rejection process did not exercise a measured path");
        BenchmarkPipeline.Require(_unexpected == 0,
            "a server-side Redis rejection returned an unexpected status");
        await _stats.DisposeAsync();
        await _limiter.DisposeAsync();
        await _baseline.DisposeAsync();
    }

    [Benchmark(Baseline = true), InvocationCount(InvocationsPerIteration)]
    public async Task Baseline_UnlimitedRequest()
    {
        _baselineCalls++;
        if (await _baseline.InvokeAsync(NextIterationAddress()) != StatusCodes.Status200OK)
        {
            _unexpected++;
        }
    }

    [Benchmark, InvocationCount(InvocationsPerIteration)]
    public async Task Redis_ServerRejected()
    {
        _limiterCalls++;
        if (await _limiter.InvokeAsync(NextIterationAddress()) != StatusCodes.Status429TooManyRequests)
        {
            _unexpected++;
        }
    }

    private IPAddress NextIterationAddress()
    {
        BenchmarkPipeline.Require(_iterationAddressIndex < _iterationAddresses.Length,
            "the Redis rejection iteration exceeded its prepared partitions");
        return _iterationAddresses[_iterationAddressIndex++];
    }

    private async Task ExhaustPartitionAsync(IPAddress address)
    {
        BenchmarkPipeline.Require(
            await _limiter.InvokeAsync(address) == StatusCodes.Status200OK,
            "the server-side Redis rejection partition could not be exhausted");
    }

    internal static IPAddress NextAddress(int group, int sequence) =>
        IPAddress.Parse($"2001:db8:{group:x}:{sequence:x}::1");
}

[MemoryDiagnoser]
public class RedisCachedRejectionBenchmarks
{
    private RedisPipeline _baseline = null!;
    private RedisPipeline _limiter = null!;
    private RedisCommandStats _stats = null!;
    private IPAddress _iterationAddress = null!;
    private long _scriptCallsBefore;
    private int _partitionSequence;
    private int _baselineCallsAtSetup;
    private int _limiterCallsAtSetup;
    private int _baselineCalls;
    private int _limiterCalls;
    private int _limiterTerminalAtSetup;
    private int _unexpected;

    [GlobalSetup]
    public async Task Setup()
    {
        var connection = RedisBenchmarkConnection.Required();
        var run = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        _baseline = RedisPipeline.CreateBaseline();
        _limiter = RedisPipeline.CreateWithPermitLimit(
            connection, $"perf:{run}:cached-reject", permitLimit: 1);
        _stats = await RedisCommandStats.ConnectAsync(connection);

        BenchmarkPipeline.Require(
            await _limiter.InvokeAsync(IPAddress.Parse("2001:db8:30:ffff::1")) == StatusCodes.Status200OK,
            "the cached Redis rejection pipeline did not warm successfully");
    }

    [IterationSetup]
    public void IterationSetup() => IterationSetupAsync().GetAwaiter().GetResult();

    private async Task IterationSetupAsync()
    {
        _iterationAddress = RedisServerRejectionBenchmarks.NextAddress(0x30, ++_partitionSequence);
        BenchmarkPipeline.Require(
            await _limiter.InvokeAsync(_iterationAddress) == StatusCodes.Status200OK,
            "the cached Redis rejection partition could not be exhausted");
        BenchmarkPipeline.Require(
            await _limiter.InvokeAsync(_iterationAddress) == StatusCodes.Status429TooManyRequests,
            "Redis did not seed the local deny cache");

        _scriptCallsBefore = await _stats.ScriptCallsAsync();
        _baselineCallsAtSetup = _baselineCalls;
        _limiterCallsAtSetup = _limiterCalls;
        _limiterTerminalAtSetup = _limiter.TerminalCalls;
    }

    [IterationCleanup]
    public void IterationCleanup() => IterationCleanupAsync().GetAwaiter().GetResult();

    private async Task IterationCleanupAsync()
    {
        var scriptCalls = await _stats.ScriptCallsAsync() - _scriptCallsBefore;
        BenchmarkPipeline.Require(scriptCalls == 0,
            $"a locally cached Redis rejection executed {scriptCalls} limiter scripts instead of zero");
        if (_limiterCalls != _limiterCallsAtSetup)
        {
            BenchmarkPipeline.Require(_limiter.TerminalCalls == _limiterTerminalAtSetup,
                "a locally cached Redis rejection reached the terminal");
        }
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        BenchmarkPipeline.Require(_baselineCalls + _limiterCalls > 0,
            "the cached Redis rejection process did not exercise a measured path");
        BenchmarkPipeline.Require(_unexpected == 0,
            "a cached Redis rejection returned an unexpected status");
        await _stats.DisposeAsync();
        await _limiter.DisposeAsync();
        await _baseline.DisposeAsync();
    }

    [Benchmark(Baseline = true), InvocationCount(1)]
    public async Task Baseline_UnlimitedRequest()
    {
        _baselineCalls++;
        if (await _baseline.InvokeAsync(_iterationAddress) != StatusCodes.Status200OK)
        {
            _unexpected++;
        }
    }

    [Benchmark, InvocationCount(1)]
    public async Task Redis_LocallyCachedRejection()
    {
        _limiterCalls++;
        if (await _limiter.InvokeAsync(_iterationAddress) != StatusCodes.Status429TooManyRequests)
        {
            _unexpected++;
        }
    }
}

internal static class RedisBenchmarkConnection
{
    public static string Required() =>
        Environment.GetEnvironmentVariable(RedisBenchmarkEnvironment.ConnectionStringVariable)
        ?? throw new InvalidOperationException("Docker is required: the Redis benchmark connection is missing.");
}
