using ApiPipeline.NET.Configuration;
using ApiPipeline.NET.Options;
using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace ApiPipeline.NET.Extensions;

/// <summary>
/// Extension methods for configuring <see cref="WebApplicationBuilder"/> with API pipeline features.
/// </summary>
public static class WebApplicationBuilderExtensions
{
    /// <summary>
    /// Configures Kestrel request limits based on <see cref="RequestLimitsOptions"/> in configuration.
    /// Also suppresses the <c>Server</c> response header when
    /// <see cref="ForwardedHeadersSettings.SuppressServerHeader"/> is <c>true</c> (default)
    /// to prevent server fingerprinting.
    /// Options are validated at startup when <see cref="ServiceCollectionExtensions.AddRequestLimits"/> is used.
    /// </summary>
    /// <param name="builder">The application builder to configure.</param>
    /// <returns>The same <see cref="WebApplicationBuilder"/> instance for chaining.</returns>
    public static WebApplicationBuilder ConfigureKestrelRequestLimits(this WebApplicationBuilder builder)
    {
        var requestLimits = new RequestLimitsOptions();
        builder.Configuration.GetSection(ApiPipelineConfigurationKeys.RequestLimits).Bind(requestLimits);

        var forwardedHeaders = new ForwardedHeadersSettings();
        builder.Configuration.GetSection(ApiPipelineConfigurationKeys.ForwardedHeaders).Bind(forwardedHeaders);

        builder.WebHost.ConfigureKestrel((context, kestrel) =>
        {
            if (forwardedHeaders.SuppressServerHeader)
            {
                kestrel.AddServerHeader = false;
            }

            if (requestLimits.Enabled)
            {
                ApplyLimits(requestLimits, kestrel.Limits);
            }
        });

        return builder;
    }

    private static void ApplyLimits(RequestLimitsOptions options, KestrelServerLimits limits)
    {
        if (options.MaxRequestBodySize is { } maxBody)
        {
            limits.MaxRequestBodySize = maxBody;
        }

        if (options.MaxRequestHeadersTotalSize is { } maxHeadersTotal)
        {
            limits.MaxRequestHeadersTotalSize = maxHeadersTotal;
        }

        if (options.MaxRequestHeaderCount is { } maxHeaderCount)
        {
            limits.MaxRequestHeaderCount = maxHeaderCount;
        }
    }
}

