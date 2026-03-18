using ApiPipeline.NET.Observability;
using ApiPipeline.NET.Options;
using Microsoft.Extensions.Options;

namespace ApiPipeline.NET.Middleware;

/// <summary>
/// ASP.NET Core middleware that emits deprecation-related headers for deprecated API versions.
/// The path prefix is configurable via <see cref="ApiVersionDeprecationOptions.PathPrefix"/>.
/// </summary>
public sealed class ApiVersionDeprecationMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IOptionsMonitor<ApiVersionDeprecationOptions> _options;
    private readonly ILogger<ApiVersionDeprecationMiddleware> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ApiVersionDeprecationMiddleware"/> class.
    /// </summary>
    /// <param name="next">The next middleware in the ASP.NET Core pipeline.</param>
    /// <param name="options">The configured API version deprecation options (supports hot-reload).</param>
    /// <param name="logger">Logger for diagnostics.</param>
    public ApiVersionDeprecationMiddleware(
        RequestDelegate next,
        IOptionsMonitor<ApiVersionDeprecationOptions> options,
        ILogger<ApiVersionDeprecationMiddleware> logger)
    {
        _next = next;
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// Executes the middleware and, when configured, appends API deprecation headers to the response
    /// via <c>HttpResponse.OnStarting</c> so headers are sent before the response body.
    /// </summary>
    /// <param name="context">The current HTTP context.</param>
    /// <returns>A task that represents the completion of request processing.</returns>
    public async Task Invoke(HttpContext context)
    {
        context.Response.OnStarting(static state =>
        {
            var (ctx, opts, logger) =
                ((HttpContext, ApiVersionDeprecationOptions, ILogger<ApiVersionDeprecationMiddleware>))state!;

            var pathPrefix = opts.PathPrefix ?? "/api";
            if (!ctx.Request.Path.StartsWithSegments(pathPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return Task.CompletedTask;
            }

            var deprecatedVersions = opts.DeprecatedVersions ?? [];
            if (!opts.Enabled || deprecatedVersions.Length == 0)
            {
                return Task.CompletedTask;
            }

            var requested = ctx.GetRequestedApiVersion();
            if (requested is null)
            {
                return Task.CompletedTask;
            }

            var requestedText = requested.ToString();
            var deprecated = deprecatedVersions.FirstOrDefault(v =>
                string.Equals(v.Version, requestedText, StringComparison.OrdinalIgnoreCase));

            if (deprecated is null)
            {
                return Task.CompletedTask;
            }

            if (deprecated.DeprecationDate is { } deprecationDate)
            {
                ctx.Response.Headers["Deprecation"] = deprecationDate.ToString("R");
            }
            else
            {
                ctx.Response.Headers["Deprecation"] = "true";
            }

            if (deprecated.SunsetDate is { } sunsetDate)
            {
                ctx.Response.Headers["Sunset"] = sunsetDate.ToString("R");
            }

            if (!string.IsNullOrWhiteSpace(deprecated.SunsetLink))
            {
                ctx.Response.Headers.Append("Link", $"<{deprecated.SunsetLink}>; rel=\"sunset\"");
            }

            ApiPipelineTelemetry.RecordDeprecationHeadersAdded(requestedText);
            logger.LogDebug("Deprecation headers added for API version {ApiVersion}", requestedText);

            return Task.CompletedTask;
        }, (context, _options.CurrentValue, _logger));

        await _next(context);
    }
}
