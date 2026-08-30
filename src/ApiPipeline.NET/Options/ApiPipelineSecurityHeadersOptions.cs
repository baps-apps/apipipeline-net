using System.ComponentModel.DataAnnotations;

namespace ApiPipeline.NET;

/// <summary>Security response headers, section <c>ApiPipeline:SecurityHeaders</c>. A header the endpoint set is never overwritten.</summary>
public sealed class ApiPipelineSecurityHeadersOptions : IValidatableObject
{
    /// <summary>Whether the headers are written. Default <c>true</c>; the phase still needs <c>WithSecurityHeaders()</c>.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary><c>Referrer-Policy</c>. Default <c>no-referrer</c>.</summary>
    public string? ReferrerPolicy { get; set; } = "no-referrer";

    /// <summary>
    /// <c>Cache-Control</c> for a response that sets none. Default <c>no-store</c> (OWASP), so shared
    /// caches do not store API responses. <c>""</c> sends none.
    /// </summary>
    public string? CacheControl { get; set; } = "no-store";

    /// <summary>Adds <c>X-Content-Type-Options: nosniff</c>. Default <c>true</c>.</summary>
    public bool AddXContentTypeOptionsNoSniff { get; set; } = true;

    /// <summary>
    /// Sends <c>Strict-Transport-Security</c> through ASP.NET Core's HSTS middleware. Default
    /// <c>true</c>. Not in Development; HTTPS requests only.
    /// </summary>
    public bool EnableStrictTransportSecurity { get; set; } = true;

    /// <summary>HSTS <c>max-age</c>, whole seconds. Default one year.</summary>
    public TimeSpan StrictTransportSecurityMaxAge { get; set; } = TimeSpan.FromDays(365);

    /// <summary>Adds <c>includeSubDomains</c>. Default <c>true</c>.</summary>
    public bool StrictTransportSecurityIncludeSubDomains { get; set; } = true;

    /// <summary>
    /// Adds <c>preload</c>. Requires <see cref="StrictTransportSecurityIncludeSubDomains"/> and a
    /// <see cref="StrictTransportSecurityMaxAge"/> of at least a year, as the preload list does.
    /// </summary>
    public bool StrictTransportSecurityPreload { get; set; }

    internal static readonly TimeSpan PreloadMinimumMaxAge = TimeSpan.FromSeconds(31_536_000);

    /// <summary>Never sends HSTS to <c>localhost</c>, <c>127.0.0.1</c> or <c>[::1]</c>. Default <c>true</c>.</summary>
    public bool StrictTransportSecurityExcludeLoopbackHosts { get; set; } = true;

    /// <summary>More hosts that never get HSTS, matched by host name without port.</summary>
    public IList<string>? StrictTransportSecurityExcludedHosts { get; set; }

    /// <summary>Adds <c>X-Frame-Options</c>. Default <c>true</c>.</summary>
    public bool AddXFrameOptions { get; set; } = true;

    /// <summary><c>X-Frame-Options</c>: <c>DENY</c> (default) or <c>SAMEORIGIN</c>.</summary>
    [RegularExpression("^(DENY|SAMEORIGIN)$", ErrorMessage = "XFrameOptionsValue must be 'DENY' or 'SAMEORIGIN'.")]
    public string XFrameOptionsValue { get; set; } = "DENY";

    /// <summary>
    /// <c>Cross-Origin-Resource-Policy</c>. Default <c>same-origin</c> (OWASP). Does not affect CORS
    /// requests. <c>""</c> sends none.
    /// </summary>
    [RegularExpression(
        "^(same-origin|same-site|cross-origin)$",
        ErrorMessage = "CrossOriginResourcePolicy must be 'same-origin', 'same-site', 'cross-origin', or empty to send no header.")]
    public string? CrossOriginResourcePolicy { get; set; } = "same-origin";

    /// <summary>
    /// <c>Content-Security-Policy</c>. Default <c>default-src 'none'; frame-ancestors 'none'</c>, which
    /// also blocks HTML the API serves (Swagger UI): set a policy on those endpoints. <c>""</c> sends none.
    /// </summary>
    public string? ContentSecurityPolicy { get; set; } = DefaultContentSecurityPolicy;

    internal const string DefaultContentSecurityPolicy = "default-src 'none'; frame-ancestors 'none'";

    /// <summary><c>Permissions-Policy</c>. Unset by default.</summary>
    public string? PermissionsPolicy { get; set; }

    /// <inheritdoc />
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (DurationRange.Check(
                StrictTransportSecurityMaxAge,
                TimeSpan.Zero,
                TimeSpan.FromDays(3650),
                nameof(StrictTransportSecurityMaxAge)) is { } failure)
        {
            yield return failure;
        }

        if (Enabled && EnableStrictTransportSecurity && StrictTransportSecurityPreload)
        {
            if (!StrictTransportSecurityIncludeSubDomains)
            {
                yield return new ValidationResult(
                    "StrictTransportSecurityPreload is true but StrictTransportSecurityIncludeSubDomains is false. " +
                    "The HSTS preload list (hstspreload.org) requires includeSubDomains. Enable it (every subdomain " +
                    "must then serve HTTPS), or turn preload off.",
                    [nameof(StrictTransportSecurityPreload), nameof(StrictTransportSecurityIncludeSubDomains)]);
            }

            if (StrictTransportSecurityMaxAge < PreloadMinimumMaxAge)
            {
                yield return new ValidationResult(
                    string.Create(
                        System.Globalization.CultureInfo.InvariantCulture,
                        $"StrictTransportSecurityPreload is true but StrictTransportSecurityMaxAge ({StrictTransportSecurityMaxAge:c}) " +
                        $"is under one year. The HSTS preload list (hstspreload.org) requires a max-age of at least " +
                        $"31536000 seconds (\"365.00:00:00\"). Raise it, or turn preload off."),
                    [nameof(StrictTransportSecurityPreload), nameof(StrictTransportSecurityMaxAge)]);
            }
        }
    }
}
