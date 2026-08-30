namespace ApiPipeline.NET.Correlation;

/// <summary>Reads the ids the correlation middleware stored on the request.</summary>
public static class HttpContextCorrelationExtensions
{
    // Object keys cannot collide with another library's Items entries.
    private static readonly object CorrelationIdKey = new();
    private static readonly object TraceIdKey = new();

    internal static void SetCorrelation(this HttpContext context, string correlationId, string? traceId)
    {
        context.Items[CorrelationIdKey] = correlationId;
        context.Items[TraceIdKey] = traceId;
    }

    /// <summary>The request's correlation id; <c>null</c> before the middleware ran.</summary>
    /// <param name="context">The current HTTP context.</param>
    public static string? GetCorrelationId(this HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Items.TryGetValue(CorrelationIdKey, out var value)
            ? value as string
            : null;
    }

    /// <summary>
    /// The request's W3C trace id; <c>null</c> before the middleware ran or without an activity. Set
    /// even when <c>EmitTraceIdHeader</c> is off.
    /// </summary>
    /// <param name="context">The current HTTP context.</param>
    public static string? GetTraceId(this HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Items.TryGetValue(TraceIdKey, out var value)
            ? value as string
            : null;
    }
}
