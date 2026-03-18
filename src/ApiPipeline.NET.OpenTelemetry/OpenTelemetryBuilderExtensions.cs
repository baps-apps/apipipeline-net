using ApiPipeline.NET.Observability;
using OpenTelemetry.Metrics;
using OpenTelemetry.NET.Extensions;
using OpenTelemetry.NET.Models;
using OpenTelemetry.Trace;

namespace ApiPipeline.NET.OpenTelemetry;

/// <summary>
/// Extension methods to add OpenTelemetry observability (tracing, metrics, and logging)
/// to an ASP.NET Core application using the OpenTelemetry.NET package.
/// Automatically registers ApiPipeline.NET's <see cref="ApiPipelineTelemetry.ActivitySource"/>
/// and <see cref="ApiPipelineTelemetry.Meter"/> so pipeline traces and metrics are exported.
/// </summary>
public static class OpenTelemetryBuilderExtensions
{
    /// <summary>
    /// Adds full OpenTelemetry observability (tracing, metrics, structured logging) to the application
    /// using the OpenTelemetry.NET package. Configuration is read from <c>AppSettings</c> and
    /// <c>OpenTelemetryOptions</c> sections in appsettings.json.
    /// </summary>
    /// <param name="builder">The <see cref="WebApplicationBuilder"/>.</param>
    /// <returns>The same builder for chaining.</returns>
    public static WebApplicationBuilder AddApiPipelineObservability(this WebApplicationBuilder builder)
    {
        builder.AddObservability();
        RegisterApiPipelineSources(builder);
        return builder;
    }

    /// <summary>
    /// Adds full OpenTelemetry observability with programmatic option overrides.
    /// Values from appsettings.json are loaded first, then the <paramref name="configureOptions"/>
    /// callback is applied before validation.
    /// </summary>
    /// <param name="builder">The <see cref="WebApplicationBuilder"/>.</param>
    /// <param name="configureOptions">
    /// Callback to override <see cref="OpenTelemetryOptions"/> values after loading from configuration.
    /// </param>
    /// <returns>The same builder for chaining.</returns>
    public static WebApplicationBuilder AddApiPipelineObservability(
        this WebApplicationBuilder builder,
        Action<OpenTelemetryOptions> configureOptions)
    {
        builder.AddObservability(configureOptions);
        RegisterApiPipelineSources(builder);
        return builder;
    }

    private static void RegisterApiPipelineSources(WebApplicationBuilder builder)
    {
        builder.Services.ConfigureOpenTelemetryTracerProvider(tracing =>
            tracing.AddSource(ApiPipelineTelemetry.ActivitySourceName));

        builder.Services.ConfigureOpenTelemetryMeterProvider(metrics =>
            metrics.AddMeter(ApiPipelineTelemetry.MeterName));
    }
}
