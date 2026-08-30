using ApiPipeline.NET.Observability;

namespace ApiPipeline.NET.Middleware;

/// <summary>Records <c>Content-Length</c> to <c>apipipeline.request.body.size</c>, by method and route template.</summary>
internal sealed class RequestSizeMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ApiPipelineMetrics _metrics;

    public RequestSizeMiddleware(RequestDelegate next, ApiPipelineMetrics metrics)
    {
        _next = next;
        _metrics = metrics;
    }

    /// <remarks>Not <c>async</c>: nothing runs after <c>next</c>.</remarks>
    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Request.ContentLength is { } length and > 0)
        {
            _metrics.RequestBodySize(
                length,
                HttpMethodTag.Normalize(context.Request.Method),
                // The template, not the path: bounded cardinality.
                (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText);
        }

        return _next(context);
    }
}
