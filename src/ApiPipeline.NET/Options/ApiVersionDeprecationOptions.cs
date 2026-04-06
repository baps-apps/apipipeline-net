using System.ComponentModel.DataAnnotations;

namespace ApiPipeline.NET.Options;

/// <summary>
/// Configuration options describing which API versions are deprecated and when.
/// </summary>
public sealed class ApiVersionDeprecationOptions
{
    /// <summary>
    /// Indicates whether API version deprecation headers are enabled.
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// The URL path prefix that triggers deprecation header inspection. Defaults to <c>/api</c>.
    /// </summary>
    [MinLength(1)]
    public string PathPrefix { get; set; } = "/api";

    /// <summary>
    /// The collection of deprecated API versions and associated metadata.
    /// </summary>
    [MinLength(0)]
    public DeprecatedVersion[] DeprecatedVersions { get; set; } = [];
}

/// <summary>
/// Represents metadata about a single deprecated API version.
/// </summary>
public sealed class DeprecatedVersion
{
    /// <summary>
    /// The API version string that is considered deprecated.
    /// </summary>
    [Required, MinLength(1)]
    public string Version { get; set; } = string.Empty;

    /// <summary>
    /// The date on which the version was, or will be, marked as deprecated.
    /// </summary>
    public DateTimeOffset? DeprecationDate { get; set; }

    /// <summary>
    /// The date on which the version will be sunset and potentially removed.
    /// </summary>
    public DateTimeOffset? SunsetDate { get; set; }

    /// <summary>
    /// An optional absolute URL providing additional information about the deprecation or sunset.
    /// Must be a valid absolute URI. Invalid values are silently skipped to prevent header injection.
    /// </summary>
    [Url]
    public string? SunsetLink { get; set; }
}
