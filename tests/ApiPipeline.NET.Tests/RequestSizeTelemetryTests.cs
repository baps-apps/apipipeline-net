using System.Diagnostics.Metrics;
using System.Text;
using ApiPipeline.NET.Pipeline;
using FluentAssertions;
using Microsoft.AspNetCore.TestHost;

namespace ApiPipeline.NET.Tests;

/// <summary>
/// The request body size histogram is only actionable if it can be broken down. An untagged global
/// histogram cannot answer "which endpoint is receiving oversized payloads?", which is the only
/// question it exists to answer. Tags must stay low-cardinality: route template and HTTP method,
/// never the raw path.
/// </summary>
public sealed class RequestSizeTelemetryTests
{
    private const string BodySizeInstrument = "apipipeline.request.body.size";

    /// <summary>
    /// Without advice, exporters fall back to OpenTelemetry's default boundaries (0 to 10,000), which
    /// are sized for milliseconds: every body over 10 KB would land in the overflow bucket. The
    /// histogram must advise byte-sized boundaries that reach past Kestrel's default 30 MB body limit.
    /// </summary>
    [Fact]
    public async Task RequestSize_Histogram_AdvisesByteSizedBucketBoundaries()
    {
        await using var app = await TestAppBuilder.CreateAppAsync(TestAppBuilder.MinimalConfig());
        using var recorder = new MetricRecorder<long>(app.Services, BodySizeInstrument);
        app.UseApiPipeline(pipeline => pipeline.WithRequestSizeTracking());
        await app.StartAsync(TestContext.Current.CancellationToken);

        var boundaries = recorder.Instruments.Should().ContainSingle()
            .Which.Should().BeOfType<Histogram<long>>().Subject.Advice?.HistogramBucketBoundaries;

        boundaries.Should().NotBeNull();
        boundaries.Should().BeInAscendingOrder().And.OnlyHaveUniqueItems();
        boundaries.Should().Contain(b => b > 10_000 && b <= 1_048_576,
            "bodies between 10 KB and 1 MB must be distinguishable");
        boundaries![^1].Should().BeGreaterThan(30_000_000,
            "Kestrel's default MaxRequestBodySize is about 30 MB");
    }

    /// <summary>
    /// The recorded measurement must carry the route template and HTTP method.
    /// </summary>
    [Fact]
    public async Task RequestSize_Histogram_IsTaggedWithRouteAndMethod()
    {
        await using var app = await TestAppBuilder.CreateAppAsync(TestAppBuilder.MinimalConfig());
        using var recorder = new MetricRecorder<long>(app.Services, BodySizeInstrument);
        app.UseApiPipeline(pipeline => pipeline.WithRequestSizeTracking());
        app.MapPost("/orders/{id}", () => Results.Ok("ok"));
        await app.StartAsync(TestContext.Current.CancellationToken);

        // StringContent sets Content-Length; JsonContent streams and leaves it unset, and the
        // middleware records only when Content-Length is present.
        await app.GetTestClient().PostAsync("/orders/42", new StringContent("{\"value\":\"payload\"}", Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken);

        var tags = recorder.Measurements.Should().ContainSingle().Subject.Tags.ToDictionary(t => t.Key, t => t.Value?.ToString());
        tags.Should().ContainKey("http.request.method").WhoseValue.Should().Be("POST");
        tags.Should().ContainKey("http.route").WhoseValue.Should().Be("/orders/{id}");
    }

    /// <summary>
    /// Kestrel accepts any token as a method, so tagging the raw method lets a caller mint a new
    /// metric series per request. Methods outside the known set must be reported as <c>_OTHER</c>
    /// (OpenTelemetry HTTP semantic conventions).
    /// </summary>
    [Fact]
    public async Task RequestSize_Histogram_ReportsUnknownMethodsAs_OTHER()
    {
        await using var app = await TestAppBuilder.CreateAppAsync(TestAppBuilder.MinimalConfig());
        using var recorder = new MetricRecorder<long>(app.Services, BodySizeInstrument);
        app.UseApiPipeline(pipeline => pipeline.WithRequestSizeTracking());
        app.MapPost("/orders/{id}", () => Results.Ok("ok"));
        await app.StartAsync(TestContext.Current.CancellationToken);

        using var request = new HttpRequestMessage(new HttpMethod("FOO1234"), "/orders/42")
        {
            Content = new StringContent("{\"value\":\"payload\"}", Encoding.UTF8, "application/json"),
        };
        await app.GetTestClient().SendAsync(request, TestContext.Current.CancellationToken);

        var tags = recorder.Measurements.Should().ContainSingle().Subject.Tags.ToDictionary(t => t.Key, t => t.Value?.ToString());
        tags.Should().ContainKey("http.request.method").WhoseValue.Should().Be("_OTHER");
    }

    /// <summary>
    /// The raw path must never become a tag value — an unbounded path space would blow up metric
    /// cardinality in the backend.
    /// </summary>
    [Fact]
    public async Task RequestSize_Histogram_DoesNotTagRawPath()
    {
        await using var app = await TestAppBuilder.CreateAppAsync(TestAppBuilder.MinimalConfig());
        using var recorder = new MetricRecorder<long>(app.Services, BodySizeInstrument);
        app.UseApiPipeline(pipeline => pipeline.WithRequestSizeTracking());
        app.MapPost("/orders/{id}", () => Results.Ok("ok"));
        await app.StartAsync(TestContext.Current.CancellationToken);

        await app.GetTestClient().PostAsync("/orders/unique-id-99999", new StringContent("{\"value\":\"payload\"}", Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken);

        recorder.Measurements.Should().NotBeEmpty();
        var allTagValues = recorder.Measurements.SelectMany(m => m.Tags)
            .Select(t => t.Value?.ToString() ?? string.Empty)
            .ToArray();
        allTagValues.Should().NotContain(v => v.Contains("unique-id-99999", StringComparison.Ordinal));
    }
}
