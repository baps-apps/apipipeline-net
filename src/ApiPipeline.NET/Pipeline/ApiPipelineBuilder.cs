using ApiPipeline.NET.Extensions;

namespace ApiPipeline.NET.Pipeline;

/// <summary>
/// Default implementation of <see cref="IApiPipelineBuilder"/>. Records middleware intents
/// and applies them in a fixed phase order when <see cref="Build"/> is called.
/// </summary>
internal sealed class ApiPipelineBuilder : IApiPipelineBuilder
{
    private readonly HashSet<string> _requested = new(StringComparer.Ordinal);
    private readonly HashSet<string> _skipped = new(StringComparer.Ordinal);

    // Fixed phase order — this is the source of truth for safe middleware ordering.
    // Authentication MUST precede ResponseCaching to prevent auth-bypass via cached responses.
    private static readonly string[] PhaseOrder =
    [
        "ForwardedHeaders",
        "RequestSizeTracking",
        "CorrelationId",
        "ExceptionHandler",
        "HttpsRedirection",
        "Cors",
        "Authentication",
        "Authorization",
        "RequestValidation",
        "RateLimiting",
        "ResponseCompression",
        "ResponseCaching",
        "OutputCaching",
        "SecurityHeaders",
        "VersionDeprecation",
    ];

    public IApiPipelineBuilder WithForwardedHeaders()     { _requested.Add("ForwardedHeaders"); return this; }
    public IApiPipelineBuilder WithRequestSizeTracking() { _requested.Add("RequestSizeTracking"); return this; }
    public IApiPipelineBuilder WithCorrelationId()        { _requested.Add("CorrelationId"); return this; }
    public IApiPipelineBuilder WithExceptionHandler()     { _requested.Add("ExceptionHandler"); return this; }
    public IApiPipelineBuilder WithHttpsRedirection()     { _requested.Add("HttpsRedirection"); return this; }
    public IApiPipelineBuilder WithCors()                 { _requested.Add("Cors"); return this; }
    public IApiPipelineBuilder WithAuthentication()       { _requested.Add("Authentication"); return this; }
    public IApiPipelineBuilder WithAuthorization()        { _requested.Add("Authorization"); return this; }
    public IApiPipelineBuilder WithRequestValidation()    { _requested.Add("RequestValidation"); return this; }
    public IApiPipelineBuilder WithRateLimiting()         { _requested.Add("RateLimiting"); return this; }
    public IApiPipelineBuilder WithResponseCompression()  { _requested.Add("ResponseCompression"); return this; }
    public IApiPipelineBuilder WithResponseCaching()      { _requested.Add("ResponseCaching"); return this; }
    public IApiPipelineBuilder WithOutputCaching()        { _requested.Add("OutputCaching"); return this; }
    public IApiPipelineBuilder WithSecurityHeaders()      { _requested.Add("SecurityHeaders"); return this; }
    public IApiPipelineBuilder WithVersionDeprecation()   { _requested.Add("VersionDeprecation"); return this; }

    public IApiPipelineBuilder SkipCorrelationId()       { _skipped.Add("CorrelationId"); return this; }
    public IApiPipelineBuilder SkipExceptionHandler()    { _skipped.Add("ExceptionHandler"); return this; }
    public IApiPipelineBuilder SkipHttpsRedirection()    { _skipped.Add("HttpsRedirection"); return this; }
    public IApiPipelineBuilder SkipVersionDeprecation()  { _skipped.Add("VersionDeprecation"); return this; }
    public IApiPipelineBuilder SkipSecurityHeaders()     { _skipped.Add("SecurityHeaders"); return this; }
    public IApiPipelineBuilder SkipCors()                { _skipped.Add("Cors"); return this; }
    public IApiPipelineBuilder SkipResponseCompression() { _skipped.Add("ResponseCompression"); return this; }
    public IApiPipelineBuilder SkipResponseCaching()     { _skipped.Add("ResponseCaching"); return this; }
    public IApiPipelineBuilder SkipOutputCaching()      { _skipped.Add("OutputCaching"); return this; }
    public IApiPipelineBuilder SkipRateLimiting()        { _skipped.Add("RateLimiting"); return this; }
    public IApiPipelineBuilder SkipForwardedHeaders()    { _skipped.Add("ForwardedHeaders"); return this; }
    public IApiPipelineBuilder SkipRequestValidation()   { _skipped.Add("RequestValidation"); return this; }
    public IApiPipelineBuilder SkipAuthentication()      { _skipped.Add("Authentication"); return this; }
    public IApiPipelineBuilder SkipAuthorization()       { _skipped.Add("Authorization"); return this; }
    public IApiPipelineBuilder SkipRequestSizeTracking() { _skipped.Add("RequestSizeTracking"); return this; }

    internal void Build(WebApplication app)
    {
        foreach (var feature in PhaseOrder)
        {
            if (!_requested.Contains(feature) || _skipped.Contains(feature))
            {
                continue;
            }

            switch (feature)
            {
                case "ForwardedHeaders":    app.UseApiPipelineForwardedHeaders(); break;
                case "RequestSizeTracking": app.UseRequestSizeTracking(); break;
                case "CorrelationId":       app.UseCorrelationId(); break;
                case "ExceptionHandler":    app.UseApiPipelineExceptionHandler(); break;
                case "HttpsRedirection":    app.UseHttpsRedirection(); break;
                case "Cors":                app.UseCors(); break;
                case "Authentication":      app.UseAuthentication(); break;
                case "Authorization":       app.UseAuthorization(); break;
                case "RequestValidation":   app.UseRequestValidation(); break;
                case "RateLimiting":        app.UseRateLimiting(); break;
                case "ResponseCompression": app.UseResponseCompression(); break;
                case "ResponseCaching":     app.UseResponseCaching(); break;
                case "OutputCaching":       app.UseOutputCaching(); break;
                case "SecurityHeaders":     app.UseSecurityHeaders(); break;
                case "VersionDeprecation":  app.UseApiVersionDeprecation(); break;
            }
        }
    }
}
