namespace ApiPipeline.NET.Options;

/// <summary>
/// Controls which services are registered by <c>AddApiPipeline</c>.
/// All flags default to <c>true</c> for a batteries-included setup.
/// </summary>
public sealed class ApiPipelineServiceRegistrationOptions
{
    /// <summary>Registers rate limiting services and options.</summary>
    public bool AddRateLimiting { get; set; } = true;
    /// <summary>Registers response compression services and options.</summary>
    public bool AddResponseCompression { get; set; } = true;
    /// <summary>Registers response caching services and options.</summary>
    public bool AddResponseCaching { get; set; } = true;
    /// <summary>Registers security headers settings.</summary>
    public bool AddSecurityHeaders { get; set; } = true;
    /// <summary>Registers CORS services and settings.</summary>
    public bool AddCors { get; set; } = true;
    /// <summary>Registers API version deprecation settings.</summary>
    public bool AddApiVersionDeprecation { get; set; } = true;
    /// <summary>Registers request limit settings for Kestrel and forms.</summary>
    public bool AddRequestLimits { get; set; } = true;
    /// <summary>Registers forwarded headers settings.</summary>
    public bool AddForwardedHeaders { get; set; } = true;
    /// <summary>Registers request size tracking middleware services.</summary>
    public bool AddRequestSizeTracking { get; set; } = true;
    /// <summary>Registers RFC 7807 problem details exception handling services.</summary>
    public bool AddExceptionHandler { get; set; } = true;
    /// <summary>Registers ASP.NET Core Output Caching services (opt-in migration from ResponseCaching).</summary>
    public bool AddOutputCaching { get; set; } = false;
}
