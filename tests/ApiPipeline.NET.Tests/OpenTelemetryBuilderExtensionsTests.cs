using System.Diagnostics;
using ApiPipeline.NET.Observability;
using ApiPipeline.NET.OpenTelemetry;
using FluentAssertions;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace ApiPipeline.NET.Tests;

/// <summary>
/// Tests for <see cref="OpenTelemetryBuilderExtensions.AddApiPipelineTelemetrySources(WebApplicationBuilder)"/>.
/// </summary>
/// <remarks>
/// This method only registers ApiPipeline.NET's sources; bootstrapping observability is the
/// OpenTelemetry.NET package's job (<c>AddObservability</c>, once per process). Because it does not call
/// <c>AddObservability</c>, it is safe to call repeatedly across tests in the shared test process.
/// </remarks>
public sealed class OpenTelemetryBuilderExtensionsTests
{
    /// <summary>
    /// Verifies the method returns the same builder for chaining.
    /// </summary>
    [Fact]
    public void AddApiPipelineTelemetrySources_Returns_Same_Builder()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();

        WebApplicationBuilder result = builder.AddApiPipelineTelemetrySources();

        result.Should().BeSameAs(builder);
    }

    /// <summary>
    /// Verifies that ApiPipeline.NET's <see cref="ApiPipelineTelemetry.ActivitySource"/> is wired into
    /// the tracer provider so its activities are sampled and exported.
    /// </summary>
    [Fact]
    public void AddApiPipelineTelemetrySources_Wires_ApiPipeline_ActivitySource()
    {
        List<Activity> exported = [];

        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Services.AddOpenTelemetry()
            .WithTracing(tracing => tracing.AddProcessor(new CapturingActivityProcessor(exported)));

        builder.AddApiPipelineTelemetrySources();

        using WebApplication app = builder.Build();
        // Resolving the provider starts the SDK's ActivityListener for the registered sources.
        TracerProvider tracerProvider = app.Services.GetRequiredService<TracerProvider>();

        using (Activity? activity = ApiPipelineTelemetry.ActivitySource.StartActivity("test"))
        {
            activity?.Stop();
        }

        tracerProvider.ForceFlush();

        exported.Should().Contain(a => a.Source.Name == ApiPipelineTelemetry.ActivitySourceName);
    }

    /// <summary>
    /// Verifies that ApiPipeline.NET's <see cref="ApiPipelineTelemetry.Meter"/> is wired into the meter
    /// provider so its instruments are collected and exported.
    /// </summary>
    [Fact]
    public void AddApiPipelineTelemetrySources_Wires_ApiPipeline_Meter()
    {
        List<string> exportedMeterNames = [];

        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Services.AddOpenTelemetry()
            .WithMetrics(metrics => metrics.AddReader(
                new BaseExportingMetricReader(new CapturingMetricExporter(exportedMeterNames))));

        builder.AddApiPipelineTelemetrySources();

        using WebApplication app = builder.Build();
        MeterProvider meterProvider = app.Services.GetRequiredService<MeterProvider>();

        ApiPipelineTelemetry.RecordCorsRejected();
        meterProvider.ForceFlush();

        exportedMeterNames.Should().Contain(ApiPipelineTelemetry.MeterName);
    }

    /// <summary>
    /// Captures ended activities into a list for assertion. OnEnd runs synchronously on activity stop.
    /// </summary>
    private sealed class CapturingActivityProcessor(List<Activity> exported) : BaseProcessor<Activity>
    {
        public override void OnEnd(Activity data) => exported.Add(data);
    }

    /// <summary>
    /// Captures the meter names of exported metrics. Export runs on <c>ForceFlush</c>.
    /// </summary>
    private sealed class CapturingMetricExporter(List<string> exportedMeterNames) : BaseExporter<Metric>
    {
        public override ExportResult Export(in Batch<Metric> batch)
        {
            foreach (Metric metric in batch)
            {
                exportedMeterNames.Add(metric.MeterName);
            }
            return ExportResult.Success;
        }
    }
}
