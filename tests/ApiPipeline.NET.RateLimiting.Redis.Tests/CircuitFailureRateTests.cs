using FluentAssertions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using StackExchange.Redis;

namespace ApiPipeline.NET.RateLimiting.Redis.Tests;

/// <summary>
/// A brownout never produces <c>FailureThreshold</c> failures in a row, so the circuit also opens
/// on a failure share of <c>FailureRatio</c> over <c>SamplingDuration</c>, given
/// <c>MinimumThroughput</c> calls.
/// </summary>
public sealed class CircuitFailureRateTests : IDisposable
{
    private readonly FakeTimeProvider _clock = new();
    private readonly ILoggerFactory _loggerFactory = LoggerFactory.Create(_ => { });

    /// <inheritdoc/>
    public void Dispose() => _loggerFactory.Dispose();

    /// <summary>Half the calls failing, never two in a row: a brownout, which opens the circuit.</summary>
    [Fact]
    public void AlternatingFailures_AtTheRatio_OpenTheCircuit()
    {
        var circuit = CreateCircuit();

        // Ends on a failure: the ratio is judged when a failure is reported.
        Alternate(circuit, calls: 21);

        circuit.IsOpen.Should().BeTrue("11 of 21 calls failed, which reaches a 0.5 failure ratio");
    }

    /// <summary>
    /// An error reply (a broken key: WRONGTYPE) is Redis answering promptly, not a brownout: however
    /// much traffic that key carries, it never opens the circuit for every other partition.
    /// </summary>
    [Fact]
    public void ServerErrorReplies_DoNotCountTowardsTheRate()
    {
        var circuit = CreateCircuit();

        for (var i = 0; i < 31; i++)
        {
            if (i % 3 == 2)
            {
                circuit.ReportSuccess();
            }
            else
            {
                circuit.ReportFailure(WrongType());
            }
        }

        circuit.IsOpen.Should().BeFalse("Redis answered every call; one key is broken, not Redis");
    }

    /// <summary>Server errors mixed with timeouts: only the timeouts are a brownout.</summary>
    [Fact]
    public void Timeouts_AmongServerErrors_CountOnlyThemselves()
    {
        var circuit = CreateCircuit();

        // 30 calls: 10 timeouts, 10 server errors, 10 successes. Counted: 10 of 30 failed.
        for (var i = 0; i < 30; i++)
        {
            switch (i % 3)
            {
                case 0: circuit.ReportFailure(new TimeoutException()); break;
                case 1: circuit.ReportFailure(WrongType()); break;
                default: circuit.ReportSuccess(); break;
            }
        }

        circuit.IsOpen.Should().BeFalse("only a third of the calls timed out, under a 0.5 ratio");
    }

    /// <summary>
    /// Error replies that mean the server as a whole cannot serve (LOADING, BUSY, OOM, CLUSTERDOWN,
    /// ...) are a brownout like timeouts: only a broken key's WRONGTYPE is left out.
    /// </summary>
    [Theory]
    [InlineData(RedisErrorKind.Loading)]
    [InlineData(RedisErrorKind.Busy)]
    [InlineData(RedisErrorKind.OutOfMemory)]
    [InlineData(RedisErrorKind.ClusterDown)]
    public void ServerWideErrorReplies_CountTowardsTheRate(RedisErrorKind kind)
    {
        var circuit = CreateCircuit();

        for (var i = 0; i < 21; i++)
        {
            if (i % 2 == 0)
            {
                circuit.ReportFailure(new RedisServerException(kind, CommandFlags.None, kind.ToString()));
            }
            else
            {
                circuit.ReportSuccess();
            }
        }

        circuit.IsOpen.Should().BeTrue($"{kind} means Redis cannot serve, so 11 of 21 calls failed");
    }

    private static RedisServerException WrongType() => new(
        RedisErrorKind.WrongType,
        CommandFlags.None,
        "WRONGTYPE Operation against a key holding the wrong kind of value");

    /// <summary>Too few calls to judge a ratio: a quiet pod is not switched over by a couple of failures.</summary>
    [Fact]
    public void BelowMinimumThroughput_StaysClosed()
    {
        var circuit = CreateCircuit();

        Alternate(circuit, calls: 18);

        circuit.IsOpen.Should().BeFalse("18 calls are below the minimum throughput of 20");
    }

    /// <summary>A failure share below the ratio is not a brownout.</summary>
    [Fact]
    public void BelowTheRatio_StaysClosed()
    {
        var circuit = CreateCircuit();

        for (var i = 0; i < 30; i++)
        {
            Report(circuit, fail: i % 3 == 0);
        }

        circuit.IsOpen.Should().BeFalse("10 of 30 calls failed, under a 0.5 failure ratio");
    }

    /// <summary>Only the sampling duration counts: old failures age out.</summary>
    [Fact]
    public void FailuresOlderThanTheSamplingDuration_DoNotCount()
    {
        var circuit = CreateCircuit();

        Alternate(circuit, calls: 12);
        _clock.Advance(TimeSpan.FromSeconds(11));
        Alternate(circuit, calls: 10);

        circuit.IsOpen.Should().BeFalse("only the last 10 calls are within the 10 s sampling duration");
    }

    /// <summary>After the circuit closes again, the brownout that opened it is not counted twice.</summary>
    [Fact]
    public void AfterClosing_TheWindowStartsEmpty()
    {
        var circuit = CreateCircuit();
        Alternate(circuit, calls: 21);
        circuit.IsOpen.Should().BeTrue();

        _clock.Advance(TimeSpan.FromSeconds(30));
        circuit.TryEnter().Should().BeTrue("this caller is the probe");
        circuit.ReportSuccess();
        circuit.IsOpen.Should().BeFalse();

        circuit.ReportFailure(new TimeoutException());

        circuit.IsOpen.Should().BeFalse("one failure in a fresh window is far below the minimum throughput");
    }

    /// <summary>The defaults: a majority of at least 20 calls over 10 seconds.</summary>
    [Fact]
    public void Options_Defaults()
    {
        var options = new ApiPipelineRedisRateLimitingOptions();

        options.FailureRatio.Should().Be(0.5);
        options.MinimumThroughput.Should().Be(20);
        options.SamplingDuration.Should().Be(TimeSpan.FromSeconds(10));
    }

    private static void Alternate(RedisCircuit circuit, int calls)
    {
        for (var i = 0; i < calls; i++)
        {
            Report(circuit, fail: i % 2 == 0);
        }
    }

    private static void Report(RedisCircuit circuit, bool fail)
    {
        if (fail)
        {
            circuit.ReportFailure(new TimeoutException());
        }
        else
        {
            circuit.ReportSuccess();
        }
    }

    // A consecutive threshold that a brownout never reaches, so only the failure rate can open it.
    private RedisCircuit CreateCircuit() => new(
        _clock,
        _loggerFactory.CreateLogger<RedisCircuit>(),
        Options.Create(new ApiPipelineRedisRateLimitingOptions
        {
            FallbackCooldown = TimeSpan.FromSeconds(30),
            FailureThreshold = 1000,
            FailureRatio = 0.5,
            MinimumThroughput = 20,
            SamplingDuration = TimeSpan.FromSeconds(10),
        }));
}
