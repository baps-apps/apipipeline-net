namespace ApiPipeline.NET.Configuration;

/// <summary>
/// Configuration section keys consumed by ApiPipeline.NET.
/// </summary>
public static class ApiPipelineConfigurationKeys
{
    /// <summary>
    /// The configuration section name for rate limiting options.
    /// </summary>
    public const string RateLimiting = "RateLimitingOptions";

    /// <summary>
    /// The configuration section name for response compression options.
    /// </summary>
    public const string ResponseCompression = "ResponseCompressionOptions";

    /// <summary>
    /// The configuration section name for response caching options.
    /// </summary>
    public const string ResponseCaching = "ResponseCachingOptions";

    /// <summary>
    /// The configuration section name for security header settings.
    /// </summary>
    public const string SecurityHeaders = "SecurityHeadersOptions";

    /// <summary>
    /// The configuration section name for CORS options.
    /// </summary>
    public const string Cors = "CorsOptions";

    /// <summary>
    /// The configuration section name for API version deprecation options.
    /// </summary>
    public const string ApiVersionDeprecation = "ApiVersionDeprecationOptions";

    /// <summary>
    /// The configuration section name for request limits options.
    /// </summary>
    public const string RequestLimits = "RequestLimitsOptions";

    /// <summary>
    /// The configuration section name for forwarded headers options.
    /// </summary>
    public const string ForwardedHeaders = "ForwardedHeadersOptions";

    /// <summary>
    /// The configuration section name for output caching options.
    /// </summary>
    public const string OutputCaching = "OutputCachingOptions";
}

