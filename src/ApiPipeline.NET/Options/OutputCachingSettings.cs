namespace ApiPipeline.NET.Options;

/// <summary>
/// Configuration options for ASP.NET Core Output Caching (the modern replacement for
/// <c>ResponseCachingMiddleware</c>). Supports distributed stores, tag-based eviction,
/// and per-endpoint revalidation semantics.
/// </summary>
public sealed class OutputCachingSettings
{
    /// <summary>
    /// Indicates whether output caching is enabled. When <c>true</c>, the
    /// <c>UseOutputCache</c> middleware is registered in the pipeline.
    /// </summary>
    public bool Enabled { get; set; } = false;
}
