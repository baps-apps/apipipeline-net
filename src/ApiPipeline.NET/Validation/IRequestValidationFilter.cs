namespace ApiPipeline.NET.Validation;

/// <summary>
/// Defines a request validation filter that runs in the ApiPipeline.NET middleware pipeline.
/// Multiple filters can be registered; they are evaluated in registration order and the first
/// failure short-circuits the pipeline with an RFC 7807 problem details response.
/// Register via <see cref="ApiPipeline.NET.Extensions.ServiceCollectionExtensions.AddRequestValidation{TFilter}"/>.
/// </summary>
public interface IRequestValidationFilter
{
    /// <summary>
    /// Validates the current request. Return <see cref="RequestValidationResult.Valid"/> to allow
    /// the request through, or <see cref="RequestValidationResult.Invalid"/> to reject it.
    /// </summary>
    ValueTask<RequestValidationResult> ValidateAsync(HttpContext context);
}
