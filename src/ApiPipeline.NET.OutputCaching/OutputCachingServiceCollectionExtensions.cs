using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.DependencyInjection;

namespace ApiPipeline.NET.OutputCaching;

/// <summary>
/// Extension methods to configure ASP.NET Core Output Caching as an alternative to
/// the in-memory <c>ResponseCaching</c> middleware. Output Caching supports distributed stores,
/// tag-based eviction, and per-endpoint revalidation semantics.
/// </summary>
public static class OutputCachingServiceCollectionExtensions
{
    /// <summary>
    /// Registers Output Caching services. Use <see cref="OutputCachingWebApplicationExtensions.UseApiPipelineOutputCaching"/>
    /// in the middleware pipeline.
    /// </summary>
    public static IServiceCollection AddApiPipelineOutputCaching(
        this IServiceCollection services,
        Action<OutputCacheOptions>? configure = null)
    {
        if (configure is not null)
        {
            services.AddOutputCache(configure);
        }
        else
        {
            services.AddOutputCache();
        }

        return services;
    }
}
