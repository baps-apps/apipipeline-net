using ApiPipeline.NET.Correlation;

namespace ApiPipeline.NET.Tests;

/// <summary>
/// The README's error-handling setup (<c>AddProblemDetails</c>, <c>UseExceptionHandler</c>,
/// <c>UseStatusCodePages</c>), kept in sync so every RFC 9457 test compiles and runs the snippet.
/// </summary>
internal static class ConsumerErrorHandling
{
    /// <summary>
    /// Registers problem details and stamps the pipeline's correlation and trace ids onto every
    /// error body, so a caller pasting a failed response into a ticket carries what is needed to
    /// pull the trace.
    /// </summary>
    public static IServiceCollection AddConsumerProblemDetails(this IServiceCollection services)
    {
        services.AddProblemDetails(options =>
        {
            options.CustomizeProblemDetails = context =>
            {
                // Error bodies are per-request and carry ids; replaying one to another caller would
                // attribute the wrong trace to their failure.
                context.HttpContext.Response.Headers.CacheControl = "no-store";

                if (context.HttpContext.GetCorrelationId() is { Length: > 0 } correlationId)
                {
                    context.ProblemDetails.Extensions["correlationId"] = correlationId;
                }

                // Assignment, not TryAdd: ASP.NET Core has already set "traceId" to the full
                // traceparent (00-<trace>-<span>-<flags>), which does not string-match the
                // X-Trace-Id header on the same response and is not what a tracing backend's search
                // box accepts. Overwrite it with the bare W3C trace id.
                if (context.HttpContext.GetTraceId() is { Length: > 0 } traceId)
                {
                    context.ProblemDetails.Extensions["traceId"] = traceId;
                }
            };
        });

        return services;
    }

    /// <summary>
    /// Converts exceptions and bare status codes from every phase after it, including a 429 from the
    /// rate limiter and a 401 from authorization, into one error shape. The ids in the body resolve
    /// wherever it is placed, because the correlation middleware writes them to
    /// <see cref="HttpContext.Items"/>; only the log line needs it inside the correlation scope.
    /// </summary>
    public static IApplicationBuilder UseConsumerErrorHandling(this IApplicationBuilder app)
    {
        app.UseExceptionHandler();
        app.UseStatusCodePages();
        return app;
    }
}
