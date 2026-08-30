using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace ApiPipeline.NET.RateLimiting.Redis;

/// <summary>
/// Reports, once, code that only calls <c>AttemptAcquire</c> on Redis limiters (a SignalR hub filter,
/// say): they always answer "no", so such code rejects every request.
/// </summary>
/// <remarks>
/// Synchronous attempts add one, asynchronous acquires remove one; the middleware's attempt-then-
/// acquire keeps it near zero. An aborted request's acquire never reaches the limiter, so with an
/// application-registered <see cref="IHttpContextAccessor"/> those attempts are ignored; without
/// one, each acquire resets the count, and only a run of <see cref="Threshold"/> is reported.
/// </remarks>
internal sealed class SynchronousAttemptMonitor
{
    internal const int Threshold = 100;

    private readonly ILogger<SynchronousAttemptMonitor> _logger;
    private readonly IHttpContextAccessor? _httpContextAccessor;
    private long _unanswered;
    private int _reported;

    public SynchronousAttemptMonitor(
        ILogger<SynchronousAttemptMonitor> logger,
        IHttpContextAccessor? httpContextAccessor = null)
    {
        _logger = logger;
        _httpContextAccessor = httpContextAccessor;
    }

    public void Attempted(string policyName)
    {
        if (Volatile.Read(ref _reported) != 0)
        {
            return;
        }

        if (_httpContextAccessor?.HttpContext?.RequestAborted.IsCancellationRequested == true)
        {
            return;
        }

        if (Interlocked.Increment(ref _unanswered) >= Threshold
            && Volatile.Read(ref _reported) == 0
            && Interlocked.Exchange(ref _reported, 1) == 0)
        {
            _logger.SynchronousAttemptsUnanswered(policyName, Threshold);
        }
    }

    public void AcquiringAsync()
    {
        if (Volatile.Read(ref _reported) != 0)
        {
            return;
        }

        if (_httpContextAccessor is null)
        {
            if (Volatile.Read(ref _unanswered) != 0)
            {
                Interlocked.Exchange(ref _unanswered, 0);
            }

            return;
        }

        long current;
        do
        {
            current = Volatile.Read(ref _unanswered);
            if (current <= 0)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref _unanswered, current - 1, current) != current);
    }
}
