using ApiPipeline.NET.ForwardedHeaders;
using ApiPipeline.NET.Middleware;
using ApiPipeline.NET.Observability;
using ApiPipeline.NET.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace ApiPipeline.NET.Pipeline;

/// <summary>One method per phase, composed through <see cref="ApiPipelineBuilder"/>.</summary>
internal static class PipelinePhases
{
    internal static IApplicationBuilder UseApiPipelineEdgeRateLimiting(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var settings = app.ApplicationServices.GetRequiredService<IOptions<ApiPipelineEdgeRateLimitingOptions>>().Value;
        if (!settings.Enabled)
        {
            return app;
        }

        var logger = app.ApplicationServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger("ApiPipeline.NET.EdgeRateLimiting");
        logger.EdgeRateLimitingEnabled(settings.PermitLimit, settings.Window);

        app.UseMiddleware<EdgeRateLimitingMiddleware>();
        return app;
    }

    internal static IApplicationBuilder UseApiPipelineCorrelationId(this IApplicationBuilder app)
    {
        app.UseMiddleware<CorrelationIdMiddleware>();
        return app;
    }

    internal static IApplicationBuilder UseApiPipelineRequestTimeouts(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var settings = app.ApplicationServices.GetRequiredService<IOptions<ApiPipelineRequestTimeoutsOptions>>().Value;
        if (!settings.Enabled)
        {
            return app;
        }

        app.UseRequestTimeouts();
        return app;
    }

    internal static IApplicationBuilder UseApiPipelineRequestSizeTracking(this IApplicationBuilder app)
    {
        app.UseMiddleware<RequestSizeMiddleware>();
        return app;
    }

    internal static IApplicationBuilder UseApiPipelineRateLimiting(this IApplicationBuilder app)
    {
        var settings = app.ApplicationServices.GetRequiredService<IOptions<ApiPipelineRateLimitingOptions>>().Value;
        if (!settings.Enabled)
        {
            return app;
        }

        var logger = app.ApplicationServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger("ApiPipeline.NET.RateLimiting");
        var policyNames = settings.Policies
            .Select(p => p.Name)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .ToArray();
        logger.RateLimitingEnabled(
            settings.DefaultPolicy,
            string.IsNullOrWhiteSpace(settings.GlobalPolicy) ? "none" : settings.GlobalPolicy,
            string.Join(", ", policyNames));

        // A 429 on a probe gets a healthy pod restarted.
        var excluded = ProbePaths.Resolve(settings.ExcludedPaths, ApiPipelineRateLimitingOptions.DefaultExcludedPaths);
        var probeNetworks = ProbePaths.ResolveNetworks(settings.ProbeNetworks);

        app.UseWhen(
            context => !ProbeExemption.Applies(context, excluded, probeNetworks),
            branch => UseRateLimiterWithHeaders(branch, settings));
        return app;
    }

    private static void UseRateLimiterWithHeaders(IApplicationBuilder app, ApiPipelineRateLimitingOptions settings)
    {
        if (settings.EmitRateLimitHeaders)
        {
            app.Use(async (context, next) =>
            {
                context.Response.OnStarting(static state =>
                {
                    var ctx = (HttpContext)state;
                    // Indexer, so a 429 that already has the field keeps one value.
                    if (ctx.Items[RateLimitContextItems.PolicyConfig] is ApiPipelineRateLimitPolicy policy
                        && ctx.Items[RateLimitContextItems.PolicyName] is string name)
                    {
                        ctx.Response.Headers[RateLimitFields.PolicyHeader] =
                            RateLimitFields.Policy(name, policy.AdvertisedQuota, policy.WindowInSeconds);
                    }
                    return Task.CompletedTask;
                }, context);
                await next(context);
            });
        }

        app.UseRateLimiter();
    }

    internal static IApplicationBuilder UseApiPipelineResponseCompression(this IApplicationBuilder app)
    {
        var settings = app.ApplicationServices
            .GetRequiredService<IOptions<ApiPipelineResponseCompressionOptions>>().Value;

        if (!settings.Enabled)
        {
            return app;
        }

        var compressionLogger = app.ApplicationServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger("ApiPipeline.NET.ResponseCompression");

        if (settings.EnableForHttps)
        {
            compressionLogger.CompressionEnabledForHttps();
        }
        else
        {
            compressionLogger.CompressionNotForHttps();
        }

        var excluded = ProbePaths.Resolve(settings.ExcludedPaths, ApiPipelineResponseCompressionOptions.DefaultExcludedPaths);

        app.UseWhen(
            context => !IsExcluded(context.Request.Path, excluded),
            branch => branch.UseResponseCompression());

        return app;
    }

    private static bool IsExcluded(PathString path, PathString[] excluded)
    {
        foreach (var candidate in excluded)
        {
            if (path.StartsWithSegments(candidate, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Response caching; with CORS on, adds <c>Vary: Origin</c> so one origin's response is not served to another.</summary>
    internal static IApplicationBuilder UseApiPipelineResponseCaching(this IApplicationBuilder app)
    {
        var settings = app.ApplicationServices.GetRequiredService<IOptions<ApiPipelineResponseCachingOptions>>().Value;
        if (!settings.Enabled)
        {
            return app;
        }

        var corsSettings = app.ApplicationServices.GetService<IOptions<ApiPipelineCorsOptions>>()?.Value;
        if (corsSettings is { Enabled: true })
        {
            app.Use(async (context, next) =>
            {
                context.Response.OnStarting(static state =>
                {
                    var ctx = (HttpContext)state;
                    if (!VariesByOrigin(ctx.Response.Headers.Vary))
                    {
                        ctx.Response.Headers.Append("Vary", "Origin");
                    }
                    return Task.CompletedTask;
                }, context);
                await next(context);
            });
        }

        app.UseResponseCaching();
        return app;
    }

    /// <summary>Whether <c>Vary</c> lists <c>Origin</c> or <c>*</c>, comparing whole field names.</summary>
    private static bool VariesByOrigin(StringValues vary)
    {
        foreach (var value in vary)
        {
            foreach (var range in value.AsSpan().Split(','))
            {
                var name = value.AsSpan()[range].Trim();
                if (name.Equals("Origin", StringComparison.OrdinalIgnoreCase) || name is "*")
                {
                    return true;
                }
            }
        }

        return false;
    }

    internal static IApplicationBuilder UseApiPipelineOutputCaching(this IApplicationBuilder app)
    {
        var settings = app.ApplicationServices.GetRequiredService<IOptions<ApiPipelineOutputCachingOptions>>().Value;
        if (!settings.Enabled)
        {
            return app;
        }

        app.UseOutputCache();
        return app;
    }

    internal static IApplicationBuilder UseApiPipelineSecurityHeaders(this IApplicationBuilder app)
    {
        var settings = app.ApplicationServices.GetRequiredService<IOptions<ApiPipelineSecurityHeadersOptions>>().Value;
        if (!settings.Enabled)
        {
            return app;
        }

        if (settings.EnableStrictTransportSecurity
            && !app.ApplicationServices.GetRequiredService<IHostEnvironment>().IsDevelopment())
        {
            app.UseHsts();
        }

        app.UseMiddleware<SecurityHeadersMiddleware>();
        return app;
    }

    internal static IApplicationBuilder UseApiPipelineCors(this IApplicationBuilder app)
    {
        var env = app.ApplicationServices.GetRequiredService<IHostEnvironment>();
        var settings = app.ApplicationServices.GetRequiredService<IOptions<ApiPipelineCorsOptions>>().Value;
        if (!settings.Enabled)
        {
            return app;
        }

        if (env.IsDevelopment() && settings.AllowAllInDevelopment)
        {
            var logger = app.ApplicationServices.GetRequiredService<ILoggerFactory>()
                .CreateLogger("ApiPipeline.NET.Cors");
            logger.CorsAllowAllActive();
        }

        app.UseCors();
        return app;
    }

    internal static IApplicationBuilder UseApiPipelineForwardedHeaders(this IApplicationBuilder app)
    {
        var settings = app.ApplicationServices.GetRequiredService<IOptions<ApiPipelineForwardedHeadersOptions>>().Value;
        if (!settings.Enabled)
        {
            return app;
        }

        var logger = app.ApplicationServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger("ApiPipeline.NET.ForwardedHeaders");

        // Throws outside Development for a trust set that lets callers choose IP, scheme or host.
        var (options, hasEffectiveTrustSet) = ForwardedHeadersConfigurator.Build(
            settings,
            app.ApplicationServices
                .GetService<IOptions<Microsoft.AspNetCore.HostFiltering.HostFilteringOptions>>()?
                .Value.AllowedHosts,
            app.ApplicationServices.GetRequiredService<IHostEnvironment>(),
            logger);

        if (hasEffectiveTrustSet)
        {
            // Without a peer address the framework trusts everyone, so skip those.
            app.UseWhen(
                static context => context.Connection.RemoteIpAddress is not null,
                branch => branch.UseForwardedHeaders(options));
        }
        else
        {
            app.UseForwardedHeaders(options);
        }

        return app;
    }
}
