namespace ApiPipeline.NET.Configuration;

/// <summary>
/// Configuration section names. Every feature reads a child of <see cref="Section"/>, for example
/// <c>ApiPipeline:Cors</c>.
/// </summary>
public static class ApiPipelineConfigurationKeys
{
    /// <summary>The top-level section: <c>ApiPipeline</c>.</summary>
    public const string Section = "ApiPipeline";

    /// <summary>Correlation ID options: <c>ApiPipeline:CorrelationId</c>.</summary>
    public const string CorrelationId = "CorrelationId";

    /// <summary>Rate limiting options: <c>ApiPipeline:RateLimiting</c>.</summary>
    public const string RateLimiting = "RateLimiting";

    /// <summary>Response compression options: <c>ApiPipeline:ResponseCompression</c>.</summary>
    public const string ResponseCompression = "ResponseCompression";

    /// <summary>Response caching options: <c>ApiPipeline:ResponseCaching</c>.</summary>
    public const string ResponseCaching = "ResponseCaching";

    /// <summary>Security header options: <c>ApiPipeline:SecurityHeaders</c>.</summary>
    public const string SecurityHeaders = "SecurityHeaders";

    /// <summary>CORS options: <c>ApiPipeline:Cors</c>.</summary>
    public const string Cors = "Cors";

    /// <summary>Request limits options: <c>ApiPipeline:RequestLimits</c>.</summary>
    public const string RequestLimits = "RequestLimits";

    /// <summary>Forwarded headers options: <c>ApiPipeline:ForwardedHeaders</c>.</summary>
    public const string ForwardedHeaders = "ForwardedHeaders";

    /// <summary>Output caching options: <c>ApiPipeline:OutputCaching</c>.</summary>
    public const string OutputCaching = "OutputCaching";

    /// <summary>Pre-authentication (edge) rate limiting options: <c>ApiPipeline:EdgeRateLimiting</c>.</summary>
    public const string EdgeRateLimiting = "EdgeRateLimiting";

    /// <summary>Request timeout options: <c>ApiPipeline:RequestTimeouts</c>.</summary>
    public const string RequestTimeouts = "RequestTimeouts";

    /// <summary>Top-level section names the package does not read, keyed by their <c>ApiPipeline</c> child; read only to warn.</summary>
    internal static readonly IReadOnlyDictionary<string, string> LegacyRootSections = new Dictionary<string, string>
    {
        ["CorrelationIdOptions"] = CorrelationId,
        ["RateLimitingOptions"] = RateLimiting,
        ["ResponseCompressionOptions"] = ResponseCompression,
        ["ResponseCachingOptions"] = ResponseCaching,
        ["SecurityHeadersOptions"] = SecurityHeaders,
        ["CorsOptions"] = Cors,
        ["RequestLimitsOptions"] = RequestLimits,
        ["ForwardedHeadersOptions"] = ForwardedHeaders,
        ["OutputCachingOptions"] = OutputCaching,
        ["EdgeRateLimitingOptions"] = EdgeRateLimiting,
        ["RequestTimeoutsOptions"] = RequestTimeouts,
    };
}
