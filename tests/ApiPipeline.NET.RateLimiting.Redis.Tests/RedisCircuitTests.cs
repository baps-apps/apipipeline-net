using FluentAssertions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using StackExchange.Redis;

namespace ApiPipeline.NET.RateLimiting.Redis.Tests;

/// <summary>
/// The circuit's cooldown and probe rules on a clock the test controls, so no test waits on real
/// time.
/// </summary>
public sealed class RedisCircuitTests : IDisposable
{
    private static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(30);

    private readonly FakeTimeProvider _clock = new();
    private readonly CapturingLoggerProvider _logs = new();
    private readonly ILoggerFactory _loggerFactory;
    private readonly RedisCircuit _circuit;

    /// <summary>
    /// A circuit with a 30 s cooldown on a fake clock that opens on the first failure (the
    /// cooldown and probe rules are the subject here), logging to <see cref="_logs"/>.
    /// </summary>
    public RedisCircuitTests()
    {
        _loggerFactory = LoggerFactory.Create(logging => logging.AddProvider(_logs).SetMinimumLevel(LogLevel.Information));
        _circuit = CreateCircuit(failureThreshold: 1);
    }

    /// <summary>Disposes the logger factory.</summary>
    public void Dispose() => _loggerFactory.Dispose();

    /// <summary>Many failures while open log one warning; nothing else is logged.</summary>
    [Fact]
    public void RepeatedFailures_OpenOnce_AndWarnOnce()
    {
        for (var i = 0; i < 5; i++)
        {
            _circuit.ReportFailure(new TimeoutException());
        }

        _circuit.IsOpen.Should().BeTrue();
        _logs.Messages.Should().ContainSingle().Which.Should().StartWith(RedisCircuit.UnavailableMessage);
    }

    /// <summary>During the cooldown nobody tries Redis; after it, exactly one caller does.</summary>
    [Fact]
    public void AfterCooldown_OneProbe()
    {
        _circuit.ReportFailure(new TimeoutException());

        _circuit.TryEnter().Should().BeFalse();
        _clock.Advance(Cooldown - TimeSpan.FromMilliseconds(1));
        _circuit.TryEnter().Should().BeFalse();

        _clock.Advance(TimeSpan.FromMilliseconds(1));
        _circuit.TryEnter().Should().BeTrue("this caller is the probe");
        _circuit.TryEnter().Should().BeFalse("only one probe per cooldown");
    }

    /// <summary>
    /// Callers racing the end of the cooldown elect one probe; during the cooldown every concurrent
    /// caller is turned away.
    /// </summary>
    [Fact]
    public void ConcurrentCallers_OneProbePerCooldown()
    {
        _circuit.ReportFailure(new TimeoutException());

        CountConcurrentEntries().Should().Be(0, "the cooldown has not passed");

        _clock.Advance(Cooldown);
        CountConcurrentEntries().Should().Be(1, "exactly one caller is the probe");
        CountConcurrentEntries().Should().Be(0, "the next probe waits another cooldown");
    }

    /// <summary>A successful probe closes the circuit and logs once.</summary>
    [Fact]
    public void ProbeSuccess_Closes_AndLogsRestoredOnce()
    {
        _circuit.ReportFailure(new TimeoutException());
        _clock.Advance(Cooldown);
        _circuit.TryEnter().Should().BeTrue();

        _circuit.ReportSuccess();
        _circuit.ReportSuccess();

        _circuit.IsOpen.Should().BeFalse();
        _circuit.TryEnter().Should().BeTrue();
        _logs.Messages.Count(m => m == RedisCircuit.RestoredMessage).Should().Be(1);
    }

    /// <summary>A failed probe keeps the circuit open for another cooldown, without another warning.</summary>
    [Fact]
    public void ProbeFailure_StaysOpen_ForAnotherCooldown()
    {
        _circuit.ReportFailure(new TimeoutException());
        _clock.Advance(Cooldown);
        _circuit.TryEnter().Should().BeTrue();

        _circuit.ReportFailure(new TimeoutException());

        _circuit.IsOpen.Should().BeTrue();
        _circuit.TryEnter().Should().BeFalse();
        _clock.Advance(Cooldown);
        _circuit.TryEnter().Should().BeTrue();
        _logs.Messages.Should().ContainSingle();
    }

    /// <summary>
    /// A probe whose caller never reports (it was cancelled) does not keep the circuit open for
    /// good: another probe is allowed a cooldown later.
    /// </summary>
    [Fact]
    public void AbandonedProbe_AnotherProbeAfterCooldown()
    {
        _circuit.ReportFailure(new TimeoutException());
        _clock.Advance(Cooldown);
        _circuit.TryEnter().Should().BeTrue();

        _clock.Advance(Cooldown);

        _circuit.TryEnter().Should().BeTrue();
    }

    /// <summary>
    /// A call that started before the circuit opened and succeeds just after does not close it: a
    /// partial outage must not flap the circuit (and the log) per request.
    /// </summary>
    [Fact]
    public void SuccessDuringFirstCooldown_DoesNotClose()
    {
        _circuit.ReportFailure(new TimeoutException());
        _clock.Advance(TimeSpan.FromMilliseconds(100));

        _circuit.ReportSuccess();

        _circuit.IsOpen.Should().BeTrue();
        _logs.Messages.Should().NotContain(RedisCircuit.RestoredMessage);
    }

    /// <summary>A success while closed logs nothing.</summary>
    [Fact]
    public void SuccessWhileClosed_LogsNothing()
    {
        _circuit.ReportSuccess();

        _circuit.IsOpen.Should().BeFalse();
        _logs.Messages.Should().BeEmpty();
    }

    /// <summary>
    /// Below the threshold a failure leaves the circuit closed and Redis in use, and is not logged
    /// as an outage: one timeout must not loosen every partition's limit.
    /// </summary>
    [Fact]
    public void FailuresBelowThreshold_StayClosed_WithoutWarning()
    {
        var circuit = CreateCircuit(failureThreshold: 3);

        circuit.ReportFailure(new TimeoutException());
        circuit.ReportFailure(new TimeoutException());

        circuit.IsOpen.Should().BeFalse();
        circuit.TryEnter().Should().BeTrue("Redis is still tried");
        _logs.Messages.Should().NotContain(m => m.StartsWith(RedisCircuit.UnavailableMessage, StringComparison.Ordinal));
    }

    /// <summary>The threshold-th failure in a row opens the circuit and warns once.</summary>
    [Fact]
    public void FailureAtThreshold_Opens_AndWarnsOnce()
    {
        var circuit = CreateCircuit(failureThreshold: 3);

        for (var i = 0; i < 3; i++)
        {
            circuit.ReportFailure(new TimeoutException());
        }

        circuit.IsOpen.Should().BeTrue();
        circuit.TryEnter().Should().BeFalse("the cooldown has begun");
        _logs.Messages.Should().ContainSingle(m => m.StartsWith(RedisCircuit.UnavailableMessage, StringComparison.Ordinal));
    }

    /// <summary>
    /// A success resets the count, so one partition that always fails, among others that succeed,
    /// never opens the circuit for everyone, however much of the traffic it carries (two thirds here):
    /// its error replies show Redis answering, so they do not count towards the failure rate either.
    /// </summary>
    [Fact]
    public void SuccessBetweenFailures_ResetsCount()
    {
        var circuit = CreateCircuit(failureThreshold: 3);

        for (var i = 0; i < 10; i++)
        {
            circuit.ReportFailure(new RedisServerException(RedisErrorKind.WrongType, CommandFlags.None, "WRONGTYPE: one bad key"));
            circuit.ReportFailure(new RedisServerException(RedisErrorKind.WrongType, CommandFlags.None, "WRONGTYPE: one bad key"));
            circuit.ReportSuccess();
        }

        circuit.IsOpen.Should().BeFalse();
        _logs.Messages.Should().BeEmpty();
    }

    /// <summary>
    /// After the circuit closes, the count starts from zero again: a single failure right after
    /// recovery must not reopen it.
    /// </summary>
    [Fact]
    public void AfterRecovery_CountStartsAgain()
    {
        var circuit = CreateCircuit(failureThreshold: 2);
        circuit.ReportFailure(new TimeoutException());
        circuit.ReportFailure(new TimeoutException());
        _clock.Advance(Cooldown);
        circuit.TryEnter().Should().BeTrue();
        circuit.ReportSuccess();

        circuit.ReportFailure(new TimeoutException());

        circuit.IsOpen.Should().BeFalse();
    }

    /// <summary>
    /// The warning says what the configured fallback does, so an Allow policy is not reported as per pod.
    /// </summary>
    [Theory]
    [InlineData(RedisFallbackBehavior.PerPod, "per pod", "not limited")]
    [InlineData(RedisFallbackBehavior.Allow, "not limited", "per pod")]
    [InlineData(RedisFallbackBehavior.Reject, "reject", "per pod")]
    public void UnavailableWarning_DescribesTheConfiguredFallback(RedisFallbackBehavior fallback, string says, string doesNotSay)
    {
        var circuit = CreateCircuit(failureThreshold: 1, fallback);

        circuit.ReportFailure(new TimeoutException());

        var warning = _logs.Messages.Should().ContainSingle().Which;
        warning.Should().StartWith(RedisCircuit.UnavailableMessage).And.Contain(says);
        warning.Should().NotContain(doesNotSay);
    }

    /// <summary>Policies with their own fallback are described too, each behaviour once.</summary>
    [Fact]
    public void UnavailableWarning_DescribesEveryFallbackInUse()
    {
        var circuit = CreateCircuit(
            failureThreshold: 1,
            RedisFallbackBehavior.PerPod,
            new Dictionary<string, RedisFallbackBehavior> { ["login"] = RedisFallbackBehavior.Reject, ["search"] = RedisFallbackBehavior.Allow });

        circuit.ReportFailure(new TimeoutException());

        _logs.Messages.Should().ContainSingle().Which.Should().Contain("per pod").And.Contain("not limited").And.Contain("reject");
    }

    /// <summary>Calls <see cref="RedisCircuit.TryEnter"/> from many threads at once; returns how many entered.</summary>
    private int CountConcurrentEntries()
    {
        var entered = 0;
        Parallel.For(0, 256, _ =>
        {
            if (_circuit.TryEnter())
            {
                Interlocked.Increment(ref entered);
            }
        });

        return entered;
    }

    private RedisCircuit CreateCircuit(
        int failureThreshold,
        RedisFallbackBehavior fallback = RedisFallbackBehavior.PerPod,
        Dictionary<string, RedisFallbackBehavior>? policyFallbacks = null) => new(
        _clock,
        _loggerFactory.CreateLogger<RedisCircuit>(),
        Options.Create(new ApiPipelineRedisRateLimitingOptions
        {
            FallbackCooldown = Cooldown,
            FailureThreshold = failureThreshold,
            Fallback = fallback,
            PolicyFallbacks = policyFallbacks ?? new Dictionary<string, RedisFallbackBehavior>(StringComparer.OrdinalIgnoreCase),
        }));
}
