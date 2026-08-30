using ApiPipeline.NET.Pipeline;

namespace Microsoft.AspNetCore.Builder;

/// <summary>Adds the ApiPipeline.NET middleware pipeline.</summary>
public static class ApiPipelineApplicationBuilderExtensions
{
    /// <summary>
    /// Adds the pipeline. Phases run in the order the <c>With*</c> methods are called; see
    /// <see cref="ApiPipelineBuilder"/>.
    /// </summary>
    /// <param name="app">The application pipeline to configure.</param>
    /// <param name="configure">Selects the phases to include.</param>
    /// <returns>The same <see cref="IApplicationBuilder"/> instance for chaining.</returns>
    /// <example>
    /// <code>
    /// app.UseApiPipeline(pipeline => pipeline
    ///     .WithForwardedHeaders()
    ///     .WithCorrelationId()
    ///     .WithSecurityHeaders()
    ///     .WithCors()
    ///     .WithAuthentication()
    ///     .WithAuthorization()
    ///     .WithRateLimiting()
    ///     .WithResponseCompression()
    ///     .WithOutputCaching()
    /// );
    /// </code>
    /// </example>
    public static IApplicationBuilder UseApiPipeline(
        this IApplicationBuilder app,
        Action<ApiPipelineBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(configure);

        var builder = new ApiPipelineBuilder();
        configure(builder);
        builder.Build(app);
        return app;
    }

}
