using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Net;
using BenchmarkDotNet.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace ApiPipeline.NET.Perf;

/// <summary>
/// A Redis that refuses connections, for the outage paths. The endpoint is a closed loopback port,
/// so nothing leaves the machine.
/// </summary>
internal static class RedisOutage
{
    public const string Unreachable = "127.0.0.1:1,connectTimeout=100,connectRetry=0";

    /// <summary>The smallest command timeout the options allow, so the fail-closed wait is short.</summary>
    public const string CommandTimeout = "00:00:00.050";

    /// <summary>Settings for an outage pipeline: a long cooldown keeps an open circuit open while measuring.</summary>
    public static Action<Dictionary<string, string?>> Configure(string fallback) => values =>
    {
        values["ApiPipeline:RateLimiting:Redis:CommandTimeout"] = CommandTimeout;
        values["ApiPipeline:RateLimiting:Redis:FallbackCooldown"] = "01:00:00";
        values["ApiPipeline:RateLimiting:Redis:Fallback"] = fallback;
    };

    /// <summary>Sends requests until <paramref name="done"/> holds; fails after 30 seconds.</summary>
    public static async Task DriveUntilAsync(RedisPipeline pipeline, IPAddress address, Func<bool> done, string failure)
    {
        var started = Stopwatch.GetTimestamp();
        while (!done())
        {
            BenchmarkPipeline.Require(Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(30), failure);
            await pipeline.InvokeAsync(address);
        }
    }
}

/// <summary>
/// Reads one pipeline's Redis metrics: the <c>store.degraded</c> gauge on demand and, when asked, the
/// <c>store.fallback</c> reasons. Scoped to that pipeline's meter factory.
/// </summary>
internal sealed class RedisOutageProbe : IDisposable
{
    private const string DegradedInstrument = "apipipeline.rate_limiting.store.degraded";
    private const string FallbackInstrument = "apipipeline.rate_limiting.store.fallback";
    private const string ReasonTag = "apipipeline.rate_limiting.store.fallback_reason";

    private readonly MeterListener _listener = new();
    private int _degraded;
    private long _failedCalls;
    private long _connecting;

    public RedisOutageProbe(RedisPipeline pipeline, bool countFallbacks)
    {
        var scope = pipeline.Services.GetRequiredService<IMeterFactory>();
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (ReferenceEquals(instrument.Meter.Scope, scope)
                && (instrument.Name == DegradedInstrument || (countFallbacks && instrument.Name == FallbackInstrument)))
            {
                listener.EnableMeasurementEvents(instrument, this);
            }
        };
        _listener.SetMeasurementEventCallback<int>(static (_, value, _, state) =>
            Volatile.Write(ref ((RedisOutageProbe)state!)._degraded, value));
        _listener.SetMeasurementEventCallback<long>(static (_, _, tags, state) =>
            ((RedisOutageProbe)state!).CountFallback(tags));
        _listener.Start();
    }

    /// <summary>Whether the circuit is open (<c>store.degraded</c> is 1).</summary>
    public bool CircuitOpen
    {
        get
        {
            _listener.RecordObservableInstruments();
            return Volatile.Read(ref _degraded) == 1;
        }
    }

    /// <summary>Fallbacks after a Redis call failed or timed out.</summary>
    public long FailedCalls => Interlocked.Read(ref _failedCalls);

    /// <summary>Fallbacks while the first connection was still being made.</summary>
    public long Connecting => Interlocked.Read(ref _connecting);

    public void Dispose() => _listener.Dispose();

    private void CountFallback(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        foreach (var tag in tags)
        {
            if (tag.Key != ReasonTag)
            {
                continue;
            }

            switch (tag.Value as string)
            {
                case "timeout" or "error":
                    Interlocked.Increment(ref _failedCalls);
                    break;
                case "connecting":
                    Interlocked.Increment(ref _connecting);
                    break;
            }
        }
    }
}

/// <summary>
/// Redis down and the circuit open: a <c>PerPod</c> policy is decided by its in-memory twin without
/// calling Redis.
/// </summary>
[MemoryDiagnoser]
public class RedisCircuitOpenBenchmarks
{
    private const int InvocationsPerIteration = 16;
    private const int ConcurrentRequests = 64;
    private static readonly IPAddress Address = IPAddress.Parse("198.51.100.80");

    private RedisPipeline _baseline = null!;
    private RedisPipeline _perPod = null!;
    private RedisOutageProbe _probe = null!;
    private int _baselineCalls;
    private int _perPodCalls;
    private int _perPodCallsAtSetup;
    private int _perPodTerminalAtSetup;
    private int _unexpected;

    [GlobalSetup]
    public async Task Setup()
    {
        var run = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        _baseline = RedisPipeline.CreateBaseline();
        _perPod = RedisPipeline.Create(
            RedisOutage.Unreachable, $"perf:{run}:circuit-open", "FixedWindow",
            configure: RedisOutage.Configure("PerPod"));
        _probe = new RedisOutageProbe(_perPod, countFallbacks: false);

        await RedisOutage.DriveUntilAsync(_perPod, Address, () => _probe.CircuitOpen,
            "the circuit did not open against an unreachable Redis");
        BenchmarkPipeline.Require(
            await _perPod.InvokeAsync(Address) == StatusCodes.Status200OK,
            "the per-pod fallback did not admit a request while the circuit was open");
    }

    [IterationSetup]
    public void IterationSetup()
    {
        _perPodCallsAtSetup = _perPodCalls;
        _perPodTerminalAtSetup = _perPod.TerminalCalls;
    }

    [IterationCleanup]
    public void IterationCleanup()
    {
        if (_perPodCalls == _perPodCallsAtSetup)
        {
            return;
        }

        BenchmarkPipeline.Require(_probe.CircuitOpen,
            "the circuit closed while the circuit-open fallback was measured");
        var measuredCalls = _perPodCalls - _perPodCallsAtSetup;
        BenchmarkPipeline.Require(_perPod.TerminalCalls - _perPodTerminalAtSetup == measuredCalls,
            $"{measuredCalls} requests under an open circuit did not all reach the terminal");
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        BenchmarkPipeline.Require(_baselineCalls + _perPodCalls > 0,
            "the circuit-open process did not exercise a measured path");
        BenchmarkPipeline.Require(_unexpected == 0,
            "a request under an open circuit returned an unexpected status");
        _probe.Dispose();
        await _perPod.DisposeAsync();
        await _baseline.DisposeAsync();
    }

    [Benchmark(Baseline = true), InvocationCount(InvocationsPerIteration)]
    public async Task Baseline_UnlimitedRequest()
    {
        _baselineCalls++;
        if (await _baseline.InvokeAsync(Address) != StatusCodes.Status200OK)
        {
            _unexpected++;
        }
    }

    [Benchmark, InvocationCount(InvocationsPerIteration)]
    public async Task Redis_CircuitOpen_PerPodFallback()
    {
        _perPodCalls++;
        if (await _perPod.InvokeAsync(Address) != StatusCodes.Status200OK)
        {
            _unexpected++;
        }
    }

    /// <summary>
    /// <see cref="ConcurrentRequests"/> requests at once under the open circuit, so contention on the
    /// circuit shows in the timing.
    /// </summary>
    [Benchmark, InvocationCount(InvocationsPerIteration)]
    public async Task Redis_CircuitOpen_PerPodFallback_Concurrent()
    {
        var pending = new Task<int>[ConcurrentRequests];
        for (var i = 0; i < pending.Length; i++)
        {
            pending[i] = Task.Run(() => _perPod.InvokeAsync(Address));
        }

        foreach (var status in await Task.WhenAll(pending))
        {
            if (status != StatusCodes.Status200OK)
            {
                _unexpected++;
            }
        }

        _perPodCalls += ConcurrentRequests;
    }
}

/// <summary>
/// Redis down under a fail-closed (<c>Reject</c>) policy, which bypasses the circuit: every request
/// waits for its own Redis call to fail, at most <c>CommandTimeout</c>, then gets a 429.
/// </summary>
[MemoryDiagnoser]
public class RedisUnavailableRejectBenchmarks
{
    private const int InvocationsPerIteration = 16;
    private static readonly IPAddress Address = IPAddress.Parse("198.51.100.81");

    private RedisPipeline _baseline = null!;
    private RedisPipeline _reject = null!;
    private RedisOutageProbe _probe = null!;
    private int _baselineCalls;
    private int _rejectCalls;
    private int _rejectCallsAtSetup;
    private int _rejectTerminalAtSetup;
    private long _failedAtSetup;
    private long _connectingAtSetup;
    private int _unexpected;

    [GlobalSetup]
    public async Task Setup()
    {
        var run = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        _baseline = RedisPipeline.CreateBaseline();
        _reject = RedisPipeline.Create(
            RedisOutage.Unreachable, $"perf:{run}:unavailable-reject", "FixedWindow",
            configure: RedisOutage.Configure("Reject"));
        _probe = new RedisOutageProbe(_reject, countFallbacks: true);

        // Past the first connect, so each measured request makes, and loses, its own Redis call.
        await RedisOutage.DriveUntilAsync(_reject, Address, () => _probe.FailedCalls > 0,
            "no Redis call failed against an unreachable Redis");
        BenchmarkPipeline.Require(
            await _reject.InvokeAsync(Address) == StatusCodes.Status429TooManyRequests,
            "a fail-closed policy did not reject while Redis was unavailable");
    }

    [IterationSetup]
    public void IterationSetup()
    {
        _rejectCallsAtSetup = _rejectCalls;
        _rejectTerminalAtSetup = _reject.TerminalCalls;
        _failedAtSetup = _probe.FailedCalls;
        _connectingAtSetup = _probe.Connecting;
    }

    [IterationCleanup]
    public void IterationCleanup()
    {
        if (_rejectCalls == _rejectCallsAtSetup)
        {
            return;
        }

        var measuredCalls = _rejectCalls - _rejectCallsAtSetup;
        var failedCalls = _probe.FailedCalls - _failedAtSetup;
        BenchmarkPipeline.Require(failedCalls == measuredCalls,
            $"{measuredCalls} fail-closed requests recorded {failedCalls} failed Redis calls");
        BenchmarkPipeline.Require(_probe.Connecting == _connectingAtSetup,
            "a measured fail-closed request was decided as still connecting");
        BenchmarkPipeline.Require(_reject.TerminalCalls == _rejectTerminalAtSetup,
            "a fail-closed request reached the terminal while Redis was unavailable");
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        BenchmarkPipeline.Require(_baselineCalls + _rejectCalls > 0,
            "the fail-closed outage process did not exercise a measured path");
        BenchmarkPipeline.Require(_unexpected == 0,
            "a fail-closed request returned an unexpected status");
        _probe.Dispose();
        await _reject.DisposeAsync();
        await _baseline.DisposeAsync();
    }

    [Benchmark(Baseline = true), InvocationCount(InvocationsPerIteration)]
    public async Task Baseline_UnlimitedRequest()
    {
        _baselineCalls++;
        if (await _baseline.InvokeAsync(Address) != StatusCodes.Status200OK)
        {
            _unexpected++;
        }
    }

    [Benchmark, InvocationCount(InvocationsPerIteration)]
    public async Task Redis_Unavailable_FailClosed()
    {
        _rejectCalls++;
        if (await _reject.InvokeAsync(Address) != StatusCodes.Status429TooManyRequests)
        {
            _unexpected++;
        }
    }
}
