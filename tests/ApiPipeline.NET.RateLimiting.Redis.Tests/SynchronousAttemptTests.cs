using System.Threading.RateLimiting;
using FluentAssertions;
using Microsoft.AspNetCore.Http;

namespace ApiPipeline.NET.RateLimiting.Redis.Tests;

/// <summary>
/// Code that only calls <see cref="RateLimiter.AttemptAcquire"/> on a Redis limiter (which always
/// answers "no") rejects every request, so the pattern is reported once.
/// </summary>
public sealed class SynchronousAttemptTests(RedisFixture redis) : IDisposable
{
    private const string Warning = "cannot reach Redis without blocking";

    private readonly CapturingLoggerProvider _logs = new();
    private ILoggerFactory? _loggerFactory;

    private ILoggerFactory Loggers => _loggerFactory ??= LoggerFactory.Create(logging => logging.AddProvider(_logs));

    /// <inheritdoc/>
    public void Dispose() => _loggerFactory?.Dispose();

    /// <summary>
    /// Synchronous attempts with no asynchronous acquire after them: every one is rejected, and the
    /// pattern is reported once, naming the policy and the fix.
    /// </summary>
    [Fact]
    public async Task OnlySynchronousAttempts_WarnOnce()
    {
        var (provider, limiter) = CreateLimiter();
        await using var _ = provider;
        using var __ = limiter;

        for (var i = 0; i < SynchronousAttemptMonitor.Threshold * 3; i++)
        {
            using var lease = limiter.AttemptAcquire(1);
            lease.IsAcquired.Should().BeFalse("a Redis limiter cannot decide synchronously");
        }

        var warnings = Warnings();
        warnings.Should().ContainSingle();
        warnings[0].Should().Contain("'strict'").And.Contain("AcquireAsync");
    }

    /// <summary>ASP.NET Core's middleware pattern (attempt, then acquire asynchronously) is the intended use.</summary>
    [Fact]
    public async Task AttemptFollowedByAcquireAsync_DoesNotWarn()
    {
        var (provider, limiter) = CreateLimiter();
        await using var _ = provider;
        using var __ = limiter;

        for (var i = 0; i < SynchronousAttemptMonitor.Threshold * 2; i++)
        {
            limiter.AttemptAcquire(1).Dispose();
            (await limiter.AcquireAsync(1, TestContext.Current.CancellationToken)).Dispose();
        }

        Warnings().Should().BeEmpty();
    }

    /// <summary>
    /// Through ASP.NET Core's real rate limiting middleware, every request is shared through Redis
    /// and nothing is reported: the middleware follows each rejected attempt with AcquireAsync.
    /// </summary>
    [Fact]
    public async Task ThroughTheMiddleware_DoesNotWarn()
    {
        var logs = new CapturingLoggerProvider();
        var config = RedisTestHost.FixedWindow(RedisTestHost.UniquePrefix(), permitLimit: 100_000);
        await using var app = await RedisTestHost.CreateAsync(redis.ConnectionString, config, logs: logs);

        for (var i = 0; i < SynchronousAttemptMonitor.Threshold * 2; i++)
        {
            (await app.GetStatusAsync()).Should().Be(System.Net.HttpStatusCode.OK);
        }

        lock (logs.Messages)
        {
            logs.Messages.Should().NotContain(m => m.Contains(Warning, StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// The middleware's pattern for a client that has already gone: the attempt, then an
    /// asynchronous acquire with the already-cancelled <c>RequestAborted</c>, which the framework's
    /// <c>RateLimiter.AcquireAsync</c> answers without calling the limiter. Counted as unanswered,
    /// these would add up over a busy pod's lifetime and report a pattern no code has.
    /// </summary>
    [Fact]
    public async Task AbortedRequests_DoNotWarn()
    {
        using var aborted = new CancellationTokenSource();
        await aborted.CancelAsync();
        var accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext { RequestAborted = aborted.Token } };
        var (provider, limiter) = CreateLimiter(accessor);
        await using var _ = provider;
        using var __ = limiter;

        for (var i = 0; i < SynchronousAttemptMonitor.Threshold * 2; i++)
        {
            limiter.AttemptAcquire(1).Dispose();
            var act = async () => await limiter.AcquireAsync(1, aborted.Token);
            await act.Should().ThrowAsync<OperationCanceledException>("the caller's own cancellation propagates");
        }

        Warnings().Should().BeEmpty();
    }

    /// <summary>
    /// A live request is still counted: code that only attempts, inside a request, is the pattern
    /// the monitor exists for.
    /// </summary>
    [Fact]
    public async Task OnlySynchronousAttempts_InALiveRequest_Warn()
    {
        var accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
        var (provider, limiter) = CreateLimiter(accessor);
        await using var _ = provider;
        using var __ = limiter;

        for (var i = 0; i < SynchronousAttemptMonitor.Threshold; i++)
        {
            limiter.AttemptAcquire(1).Dispose();
        }

        Warnings().Should().ContainSingle();
    }

    /// <summary>
    /// Without an <see cref="IHttpContextAccessor"/> an aborted request cannot be told from code that
    /// only attempts, so only a run of unanswered attempts with no asynchronous acquire between them
    /// counts. Aborted requests among ordinary traffic never make such a run, however many there are
    /// over the life of the pod.
    /// </summary>
    [Fact]
    public async Task WithoutAnAccessor_UnansweredAttemptsAmongOrdinaryTraffic_DoNotWarn()
    {
        var (provider, limiter) = CreateLimiter();
        await using var _ = provider;
        using var __ = limiter;

        for (var i = 0; i < SynchronousAttemptMonitor.Threshold * 3; i++)
        {
            limiter.AttemptAcquire(1).Dispose();
            limiter.AttemptAcquire(1).Dispose();
            (await limiter.AcquireAsync(1, TestContext.Current.CancellationToken)).Dispose();
        }

        Warnings().Should().BeEmpty();
    }

    /// <summary>
    /// The add-on does not register <see cref="IHttpContextAccessor"/> for this diagnostic: it would
    /// make every request of the application pay for an async-local write. One the application
    /// registered itself is used.
    /// </summary>
    [Fact]
    public void Registration_DoesNotAddAnHttpContextAccessor()
    {
        var services = new ServiceCollection();

        services.AddApiPipelineRedisRateLimiting(new ConfigurationBuilder().Build());

        services.Should().NotContain(d => d.ServiceType == typeof(IHttpContextAccessor));
    }

    /// <summary>A few unanswered attempts (requests caught between the two calls) are not the pattern.</summary>
    [Fact]
    public async Task BelowTheThreshold_DoesNotWarn()
    {
        var (provider, limiter) = CreateLimiter();
        await using var _ = provider;
        using var __ = limiter;

        for (var i = 0; i < SynchronousAttemptMonitor.Threshold - 1; i++)
        {
            limiter.AttemptAcquire(1).Dispose();
        }

        Warnings().Should().BeEmpty();
    }

    /// <summary>The rejection says why, for a caller that inspects the lease.</summary>
    [Fact]
    public async Task SynchronousRejection_CarriesAReason()
    {
        var (provider, limiter) = CreateLimiter();
        await using var _ = provider;
        using var __ = limiter;

        using var lease = limiter.AttemptAcquire(1);

        lease.TryGetMetadata(MetadataName.ReasonPhrase, out var reason).Should().BeTrue();
        reason.Should().Contain("AcquireAsync");
    }

    private (ServiceProvider Provider, RedisFixedWindowRateLimiter Limiter) CreateLimiter(IHttpContextAccessor? accessor = null)
    {
        var (provider, context) = RedisTestHost.CreateLimiterContext(
            _ => redis.Multiplexer,
            synchronousAttempts: new SynchronousAttemptMonitor(Loggers.CreateLogger<SynchronousAttemptMonitor>(), accessor));
        var policy = new ApiPipelineRateLimitPolicy { Name = "strict", PermitLimit = 100_000, Window = TimeSpan.FromSeconds(60) };
        return (provider, new RedisFixedWindowRateLimiter(RedisTestHost.LimiterPartition, policy, context));
    }

    private List<string> Warnings()
    {
        lock (_logs.Messages)
        {
            return [.. _logs.Messages.Where(m => m.Contains(Warning, StringComparison.Ordinal))];
        }
    }
}
