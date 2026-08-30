namespace ApiPipeline.NET.Sample;

/// <summary>
/// Example request check written as an <see cref="IEndpointFilter"/>, which works for both minimal
/// APIs and controllers (see <c>Program.cs</c>).
/// <para>
/// Returning <c>Results.Problem(...)</c> gives the same ProblemDetails shape, with ids, as every
/// other error. For model validation use <c>[ApiController]</c> or <c>AddValidation()</c> instead.
/// </para>
/// </summary>
public sealed class SampleTenantHeaderFilter : IEndpointFilter
{
    private const string TenantHeader = "X-Tenant-Id";

    /// <inheritdoc />
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        if (!context.HttpContext.Request.Headers.ContainsKey(TenantHeader))
        {
            // Describe the rule, never echo the input: the detail goes to the caller.
            return Results.Problem(
                detail: $"The {TenantHeader} header is required on this endpoint.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        return await next(context);
    }
}
