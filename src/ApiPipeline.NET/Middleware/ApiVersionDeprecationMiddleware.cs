using ApiPipeline.NET.Observability;
using ApiPipeline.NET.Options;
using ApiPipeline.NET.Versioning;
using Microsoft.Extensions.Options;

namespace ApiPipeline.NET.Middleware;

/// <summary>
/// ASP.NET Core middleware that emits deprecation-related headers for deprecated API versions.
/// Requires an <see cref="IApiVersionReader"/> registration (provided by the
/// <c>ApiPipeline.NET.Versioning</c> satellite package). If no reader is registered, the
/// middleware passes through silently.
/// </summary>
public sealed class ApiVersionDeprecationMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IOptionsMonitor<ApiVersionDeprecationOptions> _options;
    private readonly ILogger<ApiVersionDeprecationMiddleware> _logger;
    private readonly IApiVersionReader? _versionReader;

    /// <summary>
    /// Initializes a new instance of the <see cref="ApiVersionDeprecationMiddleware"/> class.
    /// </summary>
    /// <param name="next">The next middleware in the ASP.NET Core pipeline.</param>
    /// <param name="options">The configured API version deprecation options (supports hot-reload).</param>
    /// <param name="logger">Logger for diagnostics.</param>
    /// <param name="services">Service provider used to optionally resolve <see cref="IApiVersionReader"/>.</param>
    public ApiVersionDeprecationMiddleware(
        RequestDelegate next,
        IOptionsMonitor<ApiVersionDeprecationOptions> options,
        ILogger<ApiVersionDeprecationMiddleware> logger,
        IServiceProvider services)
    {
        _next = next;
        _options = options;
        _logger = logger;
        _versionReader = services.GetService<IApiVersionReader>();

        if (_versionReader is null)
        {
            logger.LogDebug(
                "ApiVersionDeprecationMiddleware: No IApiVersionReader registered. " +
                "Version deprecation headers will not be emitted. " +
                "Add the ApiPipeline.NET.Versioning package to enable this feature.");
        }
    }

    /// <summary>
    /// Executes the middleware and, when configured, appends API deprecation headers to the response
    /// via <c>HttpResponse.OnStarting</c> so headers are sent before the response body.
    /// </summary>
    /// <param name="context">The current HTTP context.</param>
    /// <returns>A task that represents the completion of request processing.</returns>
    public async Task Invoke(HttpContext context)
    {
        if (_versionReader is null)
        {
            await _next(context);
            return;
        }

        context.Response.OnStarting(static state =>
        {
            var (ctx, opts, logger, reader) =
                ((HttpContext, ApiVersionDeprecationOptions, ILogger<ApiVersionDeprecationMiddleware>, IApiVersionReader))state!;

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

            var requestedText = reader.ReadApiVersion(ctx);
            if (requestedText is null)
            {
                return Task.CompletedTask;
            }

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
                if (Uri.TryCreate(deprecated.SunsetLink, UriKind.Absolute, out _))
                {
                    ctx.Response.Headers.Append("Link", $"<{deprecated.SunsetLink}>; rel=\"sunset\"");
                }
                else
                {
                    logger.LogWarning(
                        "ApiVersionDeprecation: SunsetLink '{SunsetLink}' is not a valid absolute URI — skipped.",
                        deprecated.SunsetLink);
                }
            }

            ApiPipelineTelemetry.RecordDeprecationHeadersAdded(requestedText);
            logger.LogDebug("Deprecation headers added for API version {ApiVersion}", requestedText);

            return Task.CompletedTask;
        }, (context, _options.CurrentValue, _logger, _versionReader));

        await _next(context);
    }
}
