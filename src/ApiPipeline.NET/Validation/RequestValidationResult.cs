namespace ApiPipeline.NET.Validation;

/// <summary>
/// The result of a request validation check performed by an <see cref="IRequestValidationFilter"/>.
/// </summary>
public readonly struct RequestValidationResult
{
    private RequestValidationResult(bool isValid, int statusCode, string? detail)
    {
        IsValid = isValid;
        StatusCode = statusCode;
        Detail = detail;
    }

    /// <summary>Whether the request passed validation.</summary>
    public bool IsValid { get; }

    /// <summary>HTTP status code to return on failure. Ignored when <see cref="IsValid"/> is true.</summary>
    public int StatusCode { get; }

    /// <summary>Human-readable detail message for the problem response.</summary>
    public string? Detail { get; }

    /// <summary>A valid result — the request passes validation.</summary>
    public static RequestValidationResult Valid { get; } = new(true, 200, null);

    /// <summary>Creates an invalid result with the given status code and detail message.</summary>
    public static RequestValidationResult Invalid(int statusCode, string detail)
        => new(false, statusCode, detail);
}
