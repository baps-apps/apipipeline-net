using System.Net;
using ApiPipeline.NET.Middleware;
using ApiPipeline.NET.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ApiPipeline.NET.Extensions;

/// <summary>
/// Extension methods for enabling ApiPipeline.NET middleware on a <see cref="WebApplication"/>.
/// </summary>
public static class WebApplicationExtensions
{
    /// <summary>
    /// Adds the correlation ID middleware to the HTTP pipeline.
    /// </summary>
    /// <param name="app">The web application to configure.</param>
    /// <returns>The same <see cref="WebApplication"/> instance for chaining.</returns>
    public static WebApplication UseCorrelationId(this WebApplication app)
    {
        app.UseMiddleware<CorrelationIdMiddleware>();
        return app;
    }

    /// <summary>
    /// Enables ASP.NET Core rate limiting when <see cref="RateLimitingOptions.Enabled"/> is true.
    /// </summary>
    /// <param name="app">The web application to configure.</param>
    /// <returns>The same <see cref="WebApplication"/> instance for chaining.</returns>
    public static WebApplication UseRateLimiting(this WebApplication app)
    {
        var settings = app.Services.GetRequiredService<IOptions<RateLimitingOptions>>().Value;
        if (!settings.Enabled)
        {
            return app;
        }

        app.UseRateLimiter();
        return app;
    }

    /// <summary>
    /// Enables response compression when <see cref="ResponseCompressionSettings.Enabled"/> is true.
    /// </summary>
    /// <param name="app">The web application to configure.</param>
    /// <returns>The same <see cref="WebApplication"/> instance for chaining.</returns>
    public static WebApplication UseResponseCompression(this WebApplication app)
    {
        var settings = app.Services.GetRequiredService<IOptions<ResponseCompressionSettings>>().Value;
        if (!settings.Enabled)
        {
            return app;
        }

        if (settings.EnableForHttps)
        {
            var logger = app.Services.GetRequiredService<ILoggerFactory>()
                .CreateLogger("ApiPipeline.NET.ResponseCompression");
            logger.LogWarning(
                "ResponseCompression: EnableForHttps is true. Ensure your API never mixes " +
                "attacker-controlled input with secrets in the same compressed response (BREACH/CRIME risk).");
        }

        // Pre-compute PathString[] once at registration — avoids per-request LINQ allocation
        var excludedPaths = (settings.ExcludedPaths ?? [])
            .Select(p => new PathString(p))
            .ToArray();

        if (excludedPaths.Length == 0)
        {
            ((IApplicationBuilder)app).UseResponseCompression();
            return app;
        }

        app.UseWhen(
            context =>
            {
                var path = context.Request.Path;
                foreach (var excluded in excludedPaths)
                {
                    if (path.StartsWithSegments(excluded, StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }
                }
                return true;
            },
            branch => ((IApplicationBuilder)branch).UseResponseCompression());

        return app;
    }

    /// <summary>
    /// Enables response caching when <see cref="ResponseCachingSettings.Enabled"/> is true.
    /// </summary>
    /// <param name="app">The web application to configure.</param>
    /// <returns>The same <see cref="WebApplication"/> instance for chaining.</returns>
    public static WebApplication UseResponseCaching(this WebApplication app)
    {
        var settings = app.Services.GetRequiredService<IOptions<ResponseCachingSettings>>().Value;
        if (!settings.Enabled)
        {
            return app;
        }

        ((IApplicationBuilder)app).UseResponseCaching();
        return app;
    }

    /// <summary>
    /// Adds the security headers middleware to the HTTP pipeline.
    /// </summary>
    /// <param name="app">The web application to configure.</param>
    /// <returns>The same <see cref="WebApplication"/> instance for chaining.</returns>
    public static WebApplication UseSecurityHeaders(this WebApplication app)
    {
        app.UseMiddleware<SecurityHeadersMiddleware>();
        return app;
    }

    /// <summary>
    /// Adds the API version deprecation middleware to the HTTP pipeline.
    /// </summary>
    /// <param name="app">The web application to configure.</param>
    /// <returns>The same <see cref="WebApplication"/> instance for chaining.</returns>
    public static WebApplication UseApiVersionDeprecation(this WebApplication app)
    {
        app.UseMiddleware<ApiVersionDeprecationMiddleware>();
        return app;
    }

    /// <summary>
    /// Configures CORS policies and enables CORS for the application.
    /// </summary>
    /// <param name="app">The web application to configure.</param>
    /// <returns>The same <see cref="WebApplication"/> instance for chaining.</returns>
    public static WebApplication UseCors(this WebApplication app)
    {
        var env = app.Services.GetRequiredService<IHostEnvironment>();
        var settings = app.Services.GetRequiredService<IOptions<CorsSettings>>().Value;
        if (!settings.Enabled)
        {
            return app;
        }

        string policyName;
        if (env.IsDevelopment() && settings.AllowAllInDevelopment)
        {
            var logger = app.Services.GetRequiredService<ILoggerFactory>()
                .CreateLogger("ApiPipeline.NET.Cors");
            logger.LogWarning(
                "CORS: AllowAll policy is active (AllowAllInDevelopment=true). " +
                "All origins, methods, and headers are allowed. Do not use in production.");
            policyName = CorsPolicyNames.AllowAll;
        }
        else
        {
            policyName = CorsPolicyNames.Configured;
        }

        app.UseCors(policyName);
        return app;
    }

    /// <summary>
    /// Enables exception handling and status code pages using the <c>IProblemDetailsService</c>
    /// registered by <see cref="ServiceCollectionExtensions.AddApiPipelineExceptionHandler"/>.
    /// Produces RFC 7807 error responses with correlation ID and trace ID.
    /// Place this early in the pipeline, after <c>UseCorrelationId</c>.
    /// <para>
    /// <b>Required pipeline order:</b>
    /// <c>UseCorrelationId</c> → <c>UseApiPipelineExceptionHandler</c> → ... →
    /// <c>UseAuthentication</c> → <c>UseAuthorization</c> → <c>UseResponseCaching</c>.
    /// Placing <c>UseResponseCaching</c> before <c>UseAuthorization</c> creates an auth-bypass
    /// risk where cached responses are served without checking credentials.
    /// </para>
    /// </summary>
    /// <param name="app">The web application to configure.</param>
    /// <returns>The same <see cref="WebApplication"/> instance for chaining.</returns>
    public static WebApplication UseApiPipelineExceptionHandler(this WebApplication app)
    {
        if (app.Services.GetService<IProblemDetailsService>() is null)
        {
            throw new InvalidOperationException(
                "UseApiPipelineExceptionHandler requires AddApiPipelineExceptionHandler to be called " +
                "during service registration. Add it to your IServiceCollection setup before building the app.");
        }

        app.UseExceptionHandler();
        app.UseStatusCodePages();
        return app;
    }

    /// <summary>
    /// Configures forwarded headers so that <c>X-Forwarded-For</c>, <c>X-Forwarded-Proto</c>, and
    /// <c>X-Forwarded-Host</c> from trusted proxies are applied. Required for correct client IP resolution
    /// (used by rate limiting) when running behind a reverse proxy or Kubernetes ingress.
    /// <para>
    /// Configure <c>ForwardedHeadersOptions</c> in appsettings to set <c>KnownProxies</c>,
    /// <c>KnownNetworks</c>, and <c>ForwardLimit</c> for your deployment topology.
    /// </para>
    /// </summary>
    /// <param name="app">The web application to configure.</param>
    /// <returns>The same <see cref="WebApplication"/> instance for chaining.</returns>
    public static WebApplication UseApiPipelineForwardedHeaders(this WebApplication app)
    {
        var settings = app.Services.GetRequiredService<IOptions<ForwardedHeadersSettings>>().Value;
        if (!settings.Enabled)
        {
            return app;
        }

        var logger = app.Services.GetRequiredService<ILoggerFactory>()
            .CreateLogger("ApiPipeline.NET.ForwardedHeaders");

        if (!settings.ClearDefaultProxies
            && (settings.KnownProxies is null || settings.KnownProxies.Length == 0)
            && (settings.KnownNetworks is null || settings.KnownNetworks.Length == 0))
        {
            logger.LogWarning(
                "ForwardedHeaders is enabled but no KnownProxies or KnownNetworks are configured " +
                "and ClearDefaultProxies is false. Behind a reverse proxy (Kubernetes, Nginx, ALB), " +
                "X-Forwarded-For will be ignored and RemoteIpAddress will be the proxy IP. " +
                "This collapses rate-limiting into a single shared bucket for all clients. " +
                "Set ClearDefaultProxies: true and configure KnownNetworks for your deployment.");
        }

        var options = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor
                             | ForwardedHeaders.XForwardedProto
                             | ForwardedHeaders.XForwardedHost,
            ForwardLimit = settings.ForwardLimit
        };

        if (settings.ClearDefaultProxies)
        {
            options.KnownProxies.Clear();
            options.KnownIPNetworks.Clear();
        }

        if (settings.KnownProxies is { Length: > 0 })
        {
            foreach (var proxy in settings.KnownProxies)
            {
                if (IPAddress.TryParse(proxy, out var ip))
                {
                    options.KnownProxies.Add(ip);
                }
                else
                {
                    logger.LogWarning(
                        "ForwardedHeaders: invalid KnownProxy IP address '{Proxy}' — skipped.", proxy);
                }
            }
        }

        if (settings.KnownNetworks is { Length: > 0 })
        {
            foreach (var network in settings.KnownNetworks)
            {
                var parts = network.Split('/');
                if (parts.Length == 2
                    && IPAddress.TryParse(parts[0], out var prefix)
                    && int.TryParse(parts[1], out var prefixLength))
                {
                    var maxPrefix = prefix.AddressFamily ==
                        System.Net.Sockets.AddressFamily.InterNetworkV6 ? 128 : 32;
                    if (prefixLength < 0 || prefixLength > maxPrefix)
                    {
                        logger.LogWarning(
                            "ForwardedHeaders: invalid CIDR prefix length in '{Network}' " +
                            "(must be 0–{Max}) — skipped.", network, maxPrefix);
                        continue;
                    }
                    options.KnownIPNetworks.Add(new System.Net.IPNetwork(prefix, prefixLength));
                }
                else
                {
                    logger.LogWarning(
                        "ForwardedHeaders: could not parse KnownNetwork '{Network}' " +
                        "as a valid CIDR — skipped.", network);
                }
            }
        }

        app.UseForwardedHeaders(options);
        return app;
    }
}
