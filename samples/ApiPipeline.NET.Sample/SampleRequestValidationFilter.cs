using ApiPipeline.NET.Validation;

namespace ApiPipeline.NET.Sample;

/// <summary>
/// Demonstrates a custom request validation filter hook for ApiPipeline.NET.
/// Replace this with real OWASP/API input validation for production workloads.
/// </summary>
public sealed class SampleRequestValidationFilter : IRequestValidationFilter
{
    public ValueTask<RequestValidationResult> ValidateAsync(HttpContext context)
    {
        // Sample behavior: allow all requests.
        return ValueTask.FromResult(RequestValidationResult.Valid);
    }
}
