using ApiPipeline.NET.Observability;
using FluentAssertions;

namespace ApiPipeline.NET.Tests;

/// <summary>
/// Tests for the <see cref="ApiPipelineMetrics"/> counters and histograms.
/// </summary>
public sealed class TelemetryTests : IDisposable
{
    private readonly TestMetrics _metrics = new();
    private readonly MetricRecorder<long> _recorder;

    /// <summary>Records this test's own metrics, not another test's running in parallel.</summary>
    public TelemetryTests() => _recorder = new MetricRecorder<long>(_metrics.Factory);

    /// <inheritdoc />
    public void Dispose()
    {
        _recorder.Dispose();
        _metrics.Dispose();
    }

    /// <summary>
    /// Verifies that RecordRateLimitRejected increments the counter.
    /// </summary>
    [Fact]
    public void RecordRateLimitRejected_Increments_Counter()
    {
        _metrics.Instance.RateLimitRejected(null, null);

        Sum("apipipeline.rate_limiting.rejected").Should().Be(1);
    }

    /// <summary>
    /// Verifies that RecordRateLimitRejected with dimensions increments the counter.
    /// </summary>
    [Fact]
    public void RecordRateLimitRejected_With_Dimensions_Increments_Counter()
    {
        _metrics.Instance.RateLimitRejected("strict", "user");

        Sum("apipipeline.rate_limiting.rejected").Should().Be(1);
    }

    /// <summary>
    /// Verifies that RecordCorsRejected increments the counter.
    /// </summary>
    [Fact]
    public void RecordCorsRejected_Increments_Counter()
    {
        _metrics.Instance.CorsRejected();

        Sum("apipipeline.cors.rejected").Should().Be(1);
    }

    /// <summary>
    /// Verifies that RecordRequestBodyBytes records to the histogram.
    /// </summary>
    [Fact]
    public void RecordRequestBodyBytes_Records_Histogram()
    {
        _metrics.Instance.RequestBodySize(4096, "POST", route: null);

        _recorder.Measurements.Should().ContainSingle()
            .Which.Should().Match<RecordedMeasurement<long>>(m => m.Instrument == "apipipeline.request.body.size" && m.Value == 4096);
    }

    private long Sum(string instrumentName) => _recorder.For(instrumentName).Sum(m => m.Value);
}
