namespace ApiPipeline.NET;

/// <summary>CORS settings, section <c>ApiPipeline:Cors</c>.</summary>
public sealed class ApiPipelineCorsOptions
{
    internal static readonly string[] SafeDefaultAllowedMethods = ["GET", "POST", "PUT", "PATCH", "DELETE", "OPTIONS"];
    internal static readonly string[] SafeDefaultAllowedHeaders = ["Content-Type", "Authorization", "X-Correlation-Id"];

    /// <summary>Whether the configured policy is the default policy. Default <c>false</c>.</summary>
    public bool Enabled { get; set; }

    /// <summary>In Development only, allows any origin, method and header. Default <c>false</c>.</summary>
    public bool AllowAllInDevelopment { get; set; }

    /// <summary>
    /// Allowed origins, exactly as a browser sends them (<c>https://app.example.com</c>), or <c>*</c>.
    /// An entry that could never match (trailing slash, path, default port, pattern) fails startup.
    /// </summary>
    public IList<string>? AllowedOrigins { get; set; }

    /// <summary>Allowed methods; <c>*</c> for any. Unset: GET, POST, PUT, PATCH, DELETE, OPTIONS.</summary>
    public IList<string>? AllowedMethods { get; set; }

    /// <summary>Allowed request headers; <c>*</c> for any. Unset: Content-Type, Authorization, X-Correlation-Id.</summary>
    public IList<string>? AllowedHeaders { get; set; }

    /// <summary>Allows credentials. Requires explicit origins, not <c>*</c>.</summary>
    public bool AllowCredentials { get; set; }

    /// <summary>
    /// <c>Access-Control-Max-Age</c>, whole seconds up to one day. Unset sends none (browsers cache
    /// 5 s); browsers cap it at 2 h (Chromium) or 24 h (Firefox).
    /// </summary>
    public TimeSpan? PreflightMaxAge { get; set; }

    internal static readonly TimeSpan MaximumPreflightMaxAge = TimeSpan.FromDays(1);
}
