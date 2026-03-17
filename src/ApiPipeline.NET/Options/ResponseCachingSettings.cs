using System.ComponentModel.DataAnnotations;

namespace ApiPipeline.NET.Options;

/// <summary>
/// Configuration options for HTTP response caching.
/// </summary>
public sealed class ResponseCachingSettings
{
    /// <summary>
    /// Indicates whether response caching is enabled.
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// The approximate size limit, in bytes, of the in-memory response cache.
    /// </summary>
    [Range(0, long.MaxValue)]
    public long? SizeLimitBytes { get; set; }

    /// <summary>
    /// Indicates whether cache lookups treat paths as case-sensitive.
    /// </summary>
    public bool UseCaseSensitivePaths { get; set; } = false;
}

