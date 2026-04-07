namespace ApiPipeline.NET.Pipeline;

/// <summary>
/// Fluent builder for configuring the API middleware pipeline in a phase-enforced order.
/// Middleware is always applied in the correct sequence regardless of the order <c>With*</c>
/// methods are called. Use via <see cref="ApiPipeline.NET.Extensions.WebApplicationExtensions.UseApiPipeline"/>.
/// </summary>
public interface IApiPipelineBuilder
{
    /// <summary>Adds forwarded headers processing (Infrastructure phase).</summary>
    IApiPipelineBuilder WithForwardedHeaders();

    /// <summary>Adds correlation ID middleware (Infrastructure phase).</summary>
    IApiPipelineBuilder WithCorrelationId();

    /// <summary>Adds exception handler and status code pages (Infrastructure phase).</summary>
    IApiPipelineBuilder WithExceptionHandler();

    /// <summary>Adds HTTPS redirection (Infrastructure phase).</summary>
    IApiPipelineBuilder WithHttpsRedirection();

    /// <summary>Adds CORS (Security phase).</summary>
    IApiPipelineBuilder WithCors();

    /// <summary>Adds authentication (Auth phase).</summary>
    IApiPipelineBuilder WithAuthentication();

    /// <summary>Adds authorization (Auth phase).</summary>
    IApiPipelineBuilder WithAuthorization();

    /// <summary>Adds rate limiting (RateLimiting phase — always after auth).</summary>
    IApiPipelineBuilder WithRateLimiting();

    /// <summary>Adds response compression (Output phase).</summary>
    IApiPipelineBuilder WithResponseCompression();

    /// <summary>Adds response caching (Output phase — always after auth/authorization).</summary>
    IApiPipelineBuilder WithResponseCaching();

    /// <summary>Adds security headers (Headers phase).</summary>
    IApiPipelineBuilder WithSecurityHeaders();

    /// <summary>Adds API version deprecation headers (Headers phase).</summary>
    IApiPipelineBuilder WithVersionDeprecation();

    /// <summary>Excludes HTTPS redirection from the pipeline.</summary>
    IApiPipelineBuilder SkipHttpsRedirection();

    /// <summary>Excludes API version deprecation from the pipeline.</summary>
    IApiPipelineBuilder SkipVersionDeprecation();

    /// <summary>Excludes security headers from the pipeline.</summary>
    IApiPipelineBuilder SkipSecurityHeaders();

    /// <summary>Excludes CORS from the pipeline.</summary>
    IApiPipelineBuilder SkipCors();
}
