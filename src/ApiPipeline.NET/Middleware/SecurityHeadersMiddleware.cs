using ApiPipeline.NET.Observability;
using ApiPipeline.NET.Options;
using Microsoft.Extensions.Options;

namespace ApiPipeline.NET.Middleware;

/// <summary>
/// ASP.NET Core middleware that applies API-relevant security response headers:
/// HSTS, X-Content-Type-Options, and Referrer-Policy.
/// Headers are applied via <c>HttpResponse.OnStarting</c> to ensure they are set
/// even when downstream components write directly to the response.
/// </summary>
public sealed class SecurityHeadersMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IHostEnvironment _environment;
    private readonly IOptionsMonitor<SecurityHeadersSettings> _settings;
    private readonly ILogger<SecurityHeadersMiddleware> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SecurityHeadersMiddleware"/> class.
    /// </summary>
    /// <param name="next">The next middleware in the ASP.NET Core pipeline.</param>
    /// <param name="environment">The current hosting environment.</param>
    /// <param name="settings">The configured security header settings (supports hot-reload).</param>
    /// <param name="logger">Logger for diagnostics.</param>
    public SecurityHeadersMiddleware(
        RequestDelegate next,
        IHostEnvironment environment,
        IOptionsMonitor<SecurityHeadersSettings> settings,
        ILogger<SecurityHeadersMiddleware> logger)
    {
        _next = next;
        _environment = environment;
        _settings = settings;
        _logger = logger;
    }

    /// <summary>
    /// Executes the middleware, scheduling security headers via <c>HttpResponse.OnStarting</c>
    /// before continuing the pipeline.
    /// </summary>
    /// <param name="context">The current HTTP context.</param>
    /// <returns>A task that represents the completion of request processing.</returns>
    public async Task Invoke(HttpContext context)
    {
        var settings = _settings.CurrentValue;
        if (!settings.Enabled)
        {
            await _next(context);
            return;
        }

        var isDevelopment = _environment.IsDevelopment();

        context.Response.OnStarting(static state =>
        {
            var (ctx, s, isDev, logger) =
                ((HttpContext, SecurityHeadersSettings, bool, ILogger<SecurityHeadersMiddleware>))state;
            ApplyHeaders(ctx, s, isDev, logger);
            return Task.CompletedTask;
        }, (context, settings, isDevelopment, _logger));

        await _next(context);
    }

    private static void ApplyHeaders(
        HttpContext context,
        SecurityHeadersSettings settings,
        bool isDevelopment,
        ILogger<SecurityHeadersMiddleware> logger)
    {
        var headers = context.Response.Headers;

        if (settings.AddXContentTypeOptionsNoSniff && !headers.ContainsKey("X-Content-Type-Options"))
        {
            headers["X-Content-Type-Options"] = "nosniff";
        }

        if (!string.IsNullOrWhiteSpace(settings.ReferrerPolicy) && !headers.ContainsKey("Referrer-Policy"))
        {
            headers["Referrer-Policy"] = settings.ReferrerPolicy;
        }

        if (settings.EnableStrictTransportSecurity && !isDevelopment && !headers.ContainsKey("Strict-Transport-Security"))
        {
            var hsts = $"max-age={settings.StrictTransportSecurityMaxAgeSeconds}";
            if (settings.StrictTransportSecurityIncludeSubDomains)
            {
                hsts += "; includeSubDomains";
            }
            headers["Strict-Transport-Security"] = hsts;
        }

        ApiPipelineTelemetry.RecordSecurityHeadersApplied();
        logger.LogDebug("Security headers applied to response");
    }
}
