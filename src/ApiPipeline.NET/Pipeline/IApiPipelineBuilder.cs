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

    /// <summary>Adds request validation filters (after Auth phase, before endpoints).</summary>
    IApiPipelineBuilder WithRequestValidation();

    /// <summary>Adds rate limiting (RateLimiting phase — always after auth).</summary>
    IApiPipelineBuilder WithRateLimiting();

    /// <summary>Adds response compression (Output phase).</summary>
    IApiPipelineBuilder WithResponseCompression();

    /// <summary>Adds response caching (Output phase — always after auth/authorization).</summary>
    IApiPipelineBuilder WithResponseCaching();

    /// <summary>Adds ASP.NET Core Output Caching (Output phase — modern replacement for response caching).</summary>
    IApiPipelineBuilder WithOutputCaching();

    /// <summary>Adds security headers (Headers phase).</summary>
    IApiPipelineBuilder WithSecurityHeaders();

    /// <summary>Adds API version deprecation headers (Headers phase).</summary>
    IApiPipelineBuilder WithVersionDeprecation();

    /// <summary>Adds request body size telemetry (Infrastructure phase — after forwarded headers).</summary>
    IApiPipelineBuilder WithRequestSizeTracking();

    /// <summary>Excludes correlation ID middleware from the pipeline.</summary>
    IApiPipelineBuilder SkipCorrelationId();

    /// <summary>Excludes exception handler from the pipeline.</summary>
    IApiPipelineBuilder SkipExceptionHandler();

    /// <summary>Excludes HTTPS redirection from the pipeline.</summary>
    IApiPipelineBuilder SkipHttpsRedirection();

    /// <summary>Excludes API version deprecation from the pipeline.</summary>
    IApiPipelineBuilder SkipVersionDeprecation();

    /// <summary>Excludes security headers from the pipeline.</summary>
    IApiPipelineBuilder SkipSecurityHeaders();

    /// <summary>Excludes CORS from the pipeline.</summary>
    IApiPipelineBuilder SkipCors();

    /// <summary>Excludes response compression from the pipeline.</summary>
    IApiPipelineBuilder SkipResponseCompression();

    /// <summary>Excludes response caching from the pipeline.</summary>
    IApiPipelineBuilder SkipResponseCaching();

    /// <summary>Excludes output caching from the pipeline.</summary>
    IApiPipelineBuilder SkipOutputCaching();

    /// <summary>Excludes rate limiting from the pipeline.</summary>
    IApiPipelineBuilder SkipRateLimiting();

    /// <summary>Excludes forwarded headers processing from the pipeline.</summary>
    IApiPipelineBuilder SkipForwardedHeaders();

    /// <summary>Excludes request validation from the pipeline.</summary>
    IApiPipelineBuilder SkipRequestValidation();

    /// <summary>Excludes authentication from the pipeline.</summary>
    IApiPipelineBuilder SkipAuthentication();

    /// <summary>Excludes authorization from the pipeline.</summary>
    IApiPipelineBuilder SkipAuthorization();

    /// <summary>Excludes request size tracking from the pipeline.</summary>
    IApiPipelineBuilder SkipRequestSizeTracking();
}
