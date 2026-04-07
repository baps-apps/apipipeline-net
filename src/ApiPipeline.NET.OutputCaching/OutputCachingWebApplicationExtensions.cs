using Microsoft.AspNetCore.Builder;

namespace ApiPipeline.NET.OutputCaching;

/// <summary>
/// Extension methods for enabling Output Caching middleware.
/// </summary>
public static class OutputCachingWebApplicationExtensions
{
    /// <summary>
    /// Adds the Output Caching middleware to the pipeline. Place after
    /// <c>UseAuthorization</c> to prevent caching of unauthorized responses.
    /// </summary>
    public static WebApplication UseApiPipelineOutputCaching(this WebApplication app)
    {
        app.UseOutputCache();
        return app;
    }
}
