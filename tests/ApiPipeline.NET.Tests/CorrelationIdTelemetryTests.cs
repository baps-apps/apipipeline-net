using ApiPipeline.NET.Pipeline;
using FluentAssertions;
using Microsoft.AspNetCore.TestHost;

namespace ApiPipeline.NET.Tests;

/// <summary>
/// A rejected inbound correlation id is visible in production (metric and log), since it is a
/// caller-side bug that stops their logs and ours from being joined.
/// </summary>
public sealed class CorrelationIdTelemetryTests
{
    private const string RejectedInstrument = "apipipeline.correlation_id.rejected";

    private static async Task<WebApplication> CreateAsync()
    {
        var app = await TestAppBuilder.CreateAppAsync(TestAppBuilder.MinimalConfig());
        app.UseApiPipeline(pipeline => pipeline.WithCorrelationId());
        app.MapGet("/test", () => Results.Ok("ok"));
        await app.StartAsync();
        return app;
    }

    /// <summary>An id containing characters the allow-list rejects is counted.</summary>
    [Fact]
    public async Task MalformedInboundCorrelationId_IsCounted()
    {
        await using var app = await CreateAsync();
        using var rejected = new MetricRecorder<long>(app.Services, RejectedInstrument);
        var request = new HttpRequestMessage(HttpMethod.Get, "/test");
        request.Headers.TryAddWithoutValidation("X-Correlation-Id", "bad value with spaces");

        await app.GetTestClient().SendAsync(request, TestContext.Current.CancellationToken);

        rejected.Measurements.Sum(m => m.Value).Should().Be(1);
    }

    /// <summary>
    /// A well-formed id is adopted, not rejected — the counter must measure client errors, not
    /// traffic.
    /// </summary>
    [Fact]
    public async Task WellFormedInboundCorrelationId_IsNotCounted()
    {
        await using var app = await CreateAsync();
        using var rejected = new MetricRecorder<long>(app.Services, RejectedInstrument);
        var request = new HttpRequestMessage(HttpMethod.Get, "/test");
        request.Headers.TryAddWithoutValidation("X-Correlation-Id", "abc-123_def.456");

        await app.GetTestClient().SendAsync(request, TestContext.Current.CancellationToken);

        rejected.Measurements.Sum(m => m.Value).Should().Be(0);
    }

    /// <summary>
    /// A request with no correlation id at all is normal traffic, not a rejection.
    /// </summary>
    [Fact]
    public async Task MissingInboundCorrelationId_IsNotCounted()
    {
        await using var app = await CreateAsync();
        using var rejected = new MetricRecorder<long>(app.Services, RejectedInstrument);

        await app.GetTestClient().GetAsync("/test", TestContext.Current.CancellationToken);

        rejected.Measurements.Sum(m => m.Value).Should().Be(0);
    }
}
