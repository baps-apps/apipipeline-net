using System.ComponentModel.DataAnnotations;

namespace ApiPipeline.NET.Options;

/// <summary>
/// Configuration options for CORS behavior applied by the API pipeline.
/// When <see cref="AllowCredentials"/> is true, <see cref="AllowedOrigins"/> must be non-empty
/// (wildcard origin is not allowed by the CORS spec with credentials).
/// </summary>
public sealed class CorsSettings
{
    /// <summary>
    /// Indicates whether CORS is enabled.
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// When true in development, allows all origins, methods and headers.
    /// </summary>
    public bool AllowAllInDevelopment { get; set; } = true;

    /// <summary>
    /// The set of allowed origins. When <c>null</c> or empty, all origins are rejected in production scenarios.
    /// </summary>
    [MinLength(0)]
    public string[]? AllowedOrigins { get; set; }

    /// <summary>
    /// The set of allowed HTTP methods. A value containing <c>"*"</c> permits any method.
    /// </summary>
    [MinLength(0)]
    public string[]? AllowedMethods { get; set; } = ["GET", "POST", "PUT", "PATCH", "DELETE", "OPTIONS"];

    /// <summary>
    /// The set of allowed request headers. A value containing <c>"*"</c> permits any header.
    /// </summary>
    [MinLength(0)]
    public string[]? AllowedHeaders { get; set; } = ["*"];

    /// <summary>
    /// Indicates whether credentials are allowed on CORS requests.
    /// </summary>
    public bool AllowCredentials { get; set; } = false;
}

/// <summary>
/// Well-known CORS policy names used by the API pipeline.
/// </summary>
public static class CorsPolicyNames
{
    /// <summary>
    /// Permissive CORS policy that allows any origin, method, and header.
    /// Only registered when <see cref="CorsSettings.AllowAllInDevelopment"/> is <c>true</c>.
    /// </summary>
    public const string AllowAll = "AllowAll";

    /// <summary>
    /// Restrictive CORS policy built from <see cref="CorsSettings"/> configuration
    /// (origins, methods, headers, credentials). Used in non-development environments.
    /// </summary>
    public const string Configured = "Configured";
}

