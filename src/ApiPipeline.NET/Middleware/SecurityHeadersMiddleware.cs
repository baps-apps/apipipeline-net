using Microsoft.Extensions.Options;

namespace ApiPipeline.NET.Middleware;

/// <summary>
/// Writes the security headers in <c>OnStarting</c>, so short-circuited responses get them too;
/// a value already set is kept. HSTS is the framework's middleware, added in the same phase.
/// </summary>
internal sealed class SecurityHeadersMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ApiPipelineSecurityHeadersOptions _settings;

    // Built once; its state is the HttpContext, so no per-request closure.
    private readonly Func<object, Task> _applyHeaders;

    public SecurityHeadersMiddleware(
        RequestDelegate next,
        IOptions<ApiPipelineSecurityHeadersOptions> settings)
    {
        _next = next;
        _settings = settings.Value;
        _applyHeaders = state =>
        {
            ApplyHeaders(((HttpContext)state).Response.Headers, _settings);
            return Task.CompletedTask;
        };
    }

    /// <remarks>Not <c>async</c>: nothing runs after <c>next</c>.</remarks>
    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.Response.OnStarting(_applyHeaders, context);

        return _next(context);
    }

    private static void ApplyHeaders(IHeaderDictionary headers, ApiPipelineSecurityHeadersOptions settings)
    {
        if (settings.AddXContentTypeOptionsNoSniff && !headers.ContainsKey("X-Content-Type-Options"))
        {
            headers["X-Content-Type-Options"] = "nosniff";
        }

        if (!string.IsNullOrWhiteSpace(settings.ReferrerPolicy) && !headers.ContainsKey("Referrer-Policy"))
        {
            headers["Referrer-Policy"] = settings.ReferrerPolicy;
        }

        if (!string.IsNullOrWhiteSpace(settings.CacheControl) && !headers.ContainsKey("Cache-Control"))
        {
            headers.CacheControl = settings.CacheControl;
        }

        if (settings.AddXFrameOptions && !headers.ContainsKey("X-Frame-Options"))
        {
            headers["X-Frame-Options"] = settings.XFrameOptionsValue;
        }

        if (!string.IsNullOrWhiteSpace(settings.CrossOriginResourcePolicy)
            && !headers.ContainsKey("Cross-Origin-Resource-Policy"))
        {
            headers["Cross-Origin-Resource-Policy"] = settings.CrossOriginResourcePolicy;
        }

        if (!string.IsNullOrWhiteSpace(settings.ContentSecurityPolicy)
            && !headers.ContainsKey("Content-Security-Policy"))
        {
            headers["Content-Security-Policy"] = settings.ContentSecurityPolicy;
        }

        if (!string.IsNullOrWhiteSpace(settings.PermissionsPolicy)
            && !headers.ContainsKey("Permissions-Policy"))
        {
            headers["Permissions-Policy"] = settings.PermissionsPolicy;
        }
    }
}
