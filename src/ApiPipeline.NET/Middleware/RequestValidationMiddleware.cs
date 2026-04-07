using ApiPipeline.NET.Validation;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace ApiPipeline.NET.Middleware;

/// <summary>
/// Runs all registered <see cref="IRequestValidationFilter"/> implementations in order.
/// Short-circuits with an RFC 7807 problem details response on the first failure.
/// </summary>
public sealed class RequestValidationMiddleware : IMiddleware
{
    private readonly IEnumerable<IRequestValidationFilter> _filters;
    private readonly IProblemDetailsService _problemDetailsService;

    /// <summary>
    /// Initializes a new instance of the <see cref="RequestValidationMiddleware"/> class.
    /// </summary>
    /// <param name="filters">Registered validation filters (in registration order).</param>
    /// <param name="problemDetailsService">Service used to write RFC 7807 responses on validation failure.</param>
    public RequestValidationMiddleware(
        IEnumerable<IRequestValidationFilter> filters,
        IProblemDetailsService problemDetailsService)
    {
        _filters = filters;
        _problemDetailsService = problemDetailsService;
    }

    /// <inheritdoc />
    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        foreach (var filter in _filters)
        {
            var result = await filter.ValidateAsync(context);
            if (!result.IsValid)
            {
                context.Response.StatusCode = result.StatusCode;
                context.Response.Headers.CacheControl = "no-store";

                await _problemDetailsService.WriteAsync(new ProblemDetailsContext
                {
                    HttpContext = context,
                    ProblemDetails =
                    {
                        Status = result.StatusCode,
                        Detail = result.Detail
                    }
                });
                return;
            }
        }

        await next(context);
    }
}
