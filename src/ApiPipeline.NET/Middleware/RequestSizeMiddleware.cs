using ApiPipeline.NET.Observability;

namespace ApiPipeline.NET.Middleware;

/// <summary>
/// Thin middleware that records incoming request body size to the
/// <see cref="ApiPipelineTelemetry.RequestBodyBytesHistogram"/> metric when
/// a <c>Content-Length</c> header is present. Has no effect on the request or response.
/// Place early in the pipeline — after <c>UseForwardedHeaders</c>, before business logic.
/// </summary>
public sealed class RequestSizeMiddleware : IMiddleware
{
    /// <inheritdoc />
    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        if (context.Request.ContentLength is { } length and > 0)
        {
            ApiPipelineTelemetry.RecordRequestBodyBytes(length);
        }

        await next(context);
    }
}
