using System.Diagnostics.Metrics;
using ApiPipeline.NET.Observability;
using ApiPipeline.NET.Pipeline;
using FluentAssertions;
using Microsoft.AspNetCore.TestHost;

namespace ApiPipeline.NET.Tests;

/// <summary>
/// The package's meter comes from the host's <see cref="IMeterFactory"/> (Microsoft's guidance for
/// DI-aware libraries), so each host owns its instruments and two hosts in one process never share
/// measurements.
/// </summary>
public sealed class MeterFactoryTests
{
    /// <summary>Fails if the meter is a process-wide static rather than created by the host's factory.</summary>
    [Fact]
    public async Task Meter_IsCreatedByTheHostsMeterFactory()
    {
        // Every scope seen, not the first: other hosts in the process (tests run in parallel) record
        // the same instrument through their own factories.
        var scopes = new System.Collections.Concurrent.ConcurrentBag<object?>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument is { Meter.Name: ApiPipelineTelemetry.MeterName, Name: "apipipeline.correlation_id.rejected" })
                {
                    l.EnableMeasurementEvents(instrument);
                }
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, _, _, _) => scopes.Add(instrument.Meter.Scope));
        listener.Start();

        await using var app = await TestAppBuilder.CreateAppAsync(TestAppBuilder.MinimalConfig());
        app.UseApiPipeline(pipeline => pipeline.WithCorrelationId());
        app.MapGet("/test", () => "ok");
        await app.StartAsync(TestContext.Current.CancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/test");
        request.Headers.Add("X-Correlation-Id", "bad id with spaces");
        await app.GetTestClient().SendAsync(request, TestContext.Current.CancellationToken);

        scopes.Should().Contain(app.Services.GetRequiredService<IMeterFactory>());
    }
}
