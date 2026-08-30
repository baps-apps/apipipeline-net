using System.ComponentModel.DataAnnotations;

namespace ApiPipeline.NET;

/// <summary>Response caching settings, section <c>ApiPipeline:ResponseCaching</c>. Obsolete: use output caching.</summary>
public sealed class ApiPipelineResponseCachingOptions
{
    /// <summary>Whether response caching is on. Default <c>false</c>.</summary>
    public bool Enabled { get; set; }

    /// <summary>Approximate cache size limit, in bytes.</summary>
    [Range(1, long.MaxValue)]
    public long? SizeLimitBytes { get; set; }

    /// <summary>Whether paths are case-sensitive in the cache key.</summary>
    public bool UseCaseSensitivePaths { get; set; }
}
