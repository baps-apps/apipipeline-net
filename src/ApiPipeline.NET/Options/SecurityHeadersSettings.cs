using System.ComponentModel.DataAnnotations;

namespace ApiPipeline.NET.Options;

/// <summary>
/// Configuration options controlling which security-related HTTP response headers are applied.
/// Focused on API-relevant headers: HSTS, X-Content-Type-Options, and Referrer-Policy.
/// </summary>
public sealed class SecurityHeadersSettings
{
    /// <summary>
    /// Indicates whether security headers are enabled.
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// The value of the <c>Referrer-Policy</c> header.
    /// </summary>
    [MinLength(0)]
    public string? ReferrerPolicy { get; set; } = "no-referrer";

    /// <summary>
    /// Indicates whether the <c>X-Content-Type-Options: nosniff</c> header is added.
    /// Prevents browsers from MIME-sniffing JSON responses as executable content.
    /// </summary>
    public bool AddXContentTypeOptionsNoSniff { get; set; } = true;

    /// <summary>
    /// Indicates whether the <c>Strict-Transport-Security</c> (HSTS) header is added.
    /// Skipped automatically in Development environments.
    /// </summary>
    public bool EnableStrictTransportSecurity { get; set; } = true;

    /// <summary>
    /// The <c>max-age</c> value in seconds for the HSTS header. Defaults to one year (31536000).
    /// </summary>
    [Range(0, int.MaxValue)]
    public int StrictTransportSecurityMaxAgeSeconds { get; set; } = 31536000;

    /// <summary>
    /// Whether <c>includeSubDomains</c> is appended to the HSTS header.
    /// </summary>
    public bool StrictTransportSecurityIncludeSubDomains { get; set; } = true;
}
