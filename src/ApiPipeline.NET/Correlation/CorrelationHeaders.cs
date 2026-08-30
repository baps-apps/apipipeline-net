namespace ApiPipeline.NET.Correlation;

/// <summary>Names of the correlation headers on every response, and their log properties.</summary>
public static class CorrelationHeaders
{
    /// <summary>The correlation id: the caller's when well-formed, else generated. Forwarded downstream.</summary>
    public const string CorrelationId = "X-Correlation-Id";

    /// <summary>The W3C trace id of the request <see cref="System.Diagnostics.Activity"/>.</summary>
    public const string TraceId = "X-Trace-Id";

    /// <summary>The log-scope property with the correlation id.</summary>
    public const string CorrelationIdLogProperty = "CorrelationId";

    /// <summary>The log property with the trace id, written by the host's activity tracking.</summary>
    public const string TraceIdLogProperty = "TraceId";

    /// <summary>Both header names, for CORS exposure. A fresh array per read, so no caller can change another's.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Performance",
        "CA1819:Properties should not return arrays",
        Justification = "A new array on every read, so no caller can change another's; an array because it is passed to params string[] APIs such as CorsPolicyBuilder.WithExposedHeaders.")]
    public static string[] All => [CorrelationId, TraceId];
}
