using System.Diagnostics;
using ApiPipeline.NET.Correlation;
using ApiPipeline.NET.Observability;
using Microsoft.Extensions.Options;

namespace ApiPipeline.NET.Middleware;

/// <summary>
/// Gives every request and response a correlation id (validated inbound, or generated) and the
/// W3C trace id of the current <see cref="Activity"/>.
/// </summary>
internal sealed class CorrelationIdMiddleware
{
    public const string HeaderName = CorrelationHeaders.CorrelationId;

    public const string TraceHeaderName = CorrelationHeaders.TraceId;

    private readonly RequestDelegate _next;
    private readonly ILogger<CorrelationIdMiddleware> _logger;
    private readonly ApiPipelineCorrelationIdOptions _settings;
    private readonly ApiPipelineMetrics _metrics;

    public CorrelationIdMiddleware(
        RequestDelegate next,
        ILogger<CorrelationIdMiddleware> logger,
        IOptions<ApiPipelineCorrelationIdOptions> settings,
        ApiPipelineMetrics metrics)
    {
        _next = next;
        _logger = logger;
        _settings = settings.Value;
        _metrics = metrics;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var settings = _settings;
        var correlationId = ResolveCorrelationId(context, settings);

        var activity = Activity.Current;
        var traceId = activity?.TraceId.ToString();

        context.SetCorrelation(correlationId, traceId);

        // One allocation: the logging scope and the header callback's state.
        var scope = new CorrelationScope(context, correlationId, traceId, settings.EmitTraceIdHeader);

        // OnStarting, so the headers survive an exception handler's Response.Clear().
        context.Response.OnStarting(static state =>
        {
            var scope = (CorrelationScope)state;
            var headers = scope.Context.Response.Headers;
            headers[CorrelationHeaders.CorrelationId] = scope.CorrelationId;
            if (scope.EmitTraceId && scope.TraceId is { } traceId)
            {
                headers[CorrelationHeaders.TraceId] = traceId;
            }

            return Task.CompletedTask;
        }, scope);

        activity?.SetTag(ApiPipelineTelemetry.CorrelationIdTagName, correlationId);

        using (_logger.BeginScope(scope))
        {
            await _next(context);
        }
    }

    private string ResolveCorrelationId(HttpContext context, ApiPipelineCorrelationIdOptions settings)
    {
        if (!settings.AcceptInboundCorrelationId)
        {
            return NewCorrelationId();
        }

        if (!context.Request.Headers.TryGetValue(CorrelationHeaders.CorrelationId, out var existing))
        {
            return NewCorrelationId();
        }

        // A repeated header joins with ',', which IsWellFormed rejects.
        var incoming = existing.ToString();
        if (incoming.Length > 0 && incoming.Length <= settings.MaxLength && IsWellFormed(incoming))
        {
            return incoming;
        }

        var generated = NewCorrelationId();
        _metrics.CorrelationIdRejected();
        _logger.CorrelationIdRejected(generated);
        return generated;
    }

    /// <summary>Not the trace id, so generated and caller-supplied ids stay distinguishable.</summary>
    private static string NewCorrelationId() => Guid.NewGuid().ToString("N");

    /// <summary><c>[A-Za-z0-9_.-]</c> only, so untrusted ids cannot inject into logs or headers.</summary>
    private static bool IsWellFormed(ReadOnlySpan<char> value)
    {
        foreach (var c in value)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.'))
            {
                return false;
            }
        }

        return true;
    }

}
