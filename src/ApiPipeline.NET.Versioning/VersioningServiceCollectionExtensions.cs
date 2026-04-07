using ApiPipeline.NET.Extensions;
using ApiPipeline.NET.Versioning.AspVersioning;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ApiPipeline.NET.Versioning;

/// <summary>
/// Extension methods for integrating <c>Asp.Versioning.Mvc</c> with ApiPipeline.NET.
/// </summary>
public static class VersioningServiceCollectionExtensions
{
    /// <summary>
    /// Registers API version deprecation services using <c>Asp.Versioning.Mvc</c> for
    /// version resolution. Call this instead of <c>AddApiVersionDeprecation</c> when using
    /// the <c>Asp.Versioning</c> package.
    /// </summary>
    public static IServiceCollection AddApiPipelineVersioning(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddApiVersionDeprecation(configuration);
        services.TryAddSingleton<IApiVersionReader, AspVersioningApiVersionReader>();
        return services;
    }
}
