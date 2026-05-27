using ApiPipeline.NET.Observability;
using Microsoft.AspNetCore.Builder;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace ApiPipeline.NET.OpenTelemetry;

/// <summary>
/// Extension methods that plug ApiPipeline.NET's <see cref="ApiPipelineTelemetry.ActivitySource"/> and
/// <see cref="ApiPipelineTelemetry.Meter"/> into an application's existing OpenTelemetry providers, so
/// pipeline traces and metrics are exported.
/// </summary>
/// <remarks>
/// Bootstrapping observability (tracing, metrics, structured logging) is the responsibility of the
/// OpenTelemetry.NET package — call <c>builder.AddObservability()</c> for that. This package only
/// contributes ApiPipeline.NET's own telemetry sources on top of that bootstrap.
/// </remarks>
public static class OpenTelemetryBuilderExtensions
{
    /// <summary>
    /// Registers ApiPipeline.NET's <see cref="ApiPipelineTelemetry.ActivitySource"/> and
    /// <see cref="ApiPipelineTelemetry.Meter"/> with the OpenTelemetry tracer and meter providers.
    /// </summary>
    /// <remarks>
    /// Call this after the application has bootstrapped OpenTelemetry via <c>builder.AddObservability()</c>
    /// (from the OpenTelemetry.NET package). It adds ApiPipeline.NET's traces and metrics to the existing
    /// providers without re-running the bootstrap.
    /// <para>
    /// Logs require no registration here: ApiPipeline.NET middleware writes through <c>ILogger</c>, which the
    /// host's logging pipeline (configured by <c>AddObservability</c>) already captures and exports.
    /// </para>
    /// Typical usage:
    /// <code>
    /// builder.AddObservability();              // OpenTelemetry.NET — host bootstrap (once per process)
    /// builder.AddApiPipelineTelemetrySources(); // ApiPipeline.NET — adds its ActivitySource and Meter
    /// </code>
    /// </remarks>
    /// <param name="builder">The <see cref="WebApplicationBuilder"/>.</param>
    /// <returns>The same builder for chaining.</returns>
    public static WebApplicationBuilder AddApiPipelineTelemetrySources(this WebApplicationBuilder builder)
    {
        builder.Services.ConfigureOpenTelemetryTracerProvider(tracing =>
            tracing.AddSource(ApiPipelineTelemetry.ActivitySourceName));

        builder.Services.ConfigureOpenTelemetryMeterProvider(metrics =>
            metrics.AddMeter(ApiPipelineTelemetry.MeterName));

        return builder;
    }
}
