using System.Diagnostics.Metrics;
using ApiPipeline.NET.Observability;
using FluentAssertions;

namespace ApiPipeline.NET.Tests;

/// <summary>
/// Tests for <see cref="ApiPipelineTelemetry"/> counters and histograms.
/// </summary>
public sealed class TelemetryTests : IDisposable
{
    private readonly MeterListener _listener;
    private readonly Dictionary<string, long> _counters = new();
    private readonly List<(string Name, long Value)> _histogramRecordings = [];

    /// <summary>
    /// Initializes the <see cref="MeterListener"/> to capture telemetry metrics.
    /// </summary>
    public TelemetryTests()
    {
        _listener = new MeterListener();
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == ApiPipelineTelemetry.MeterName)
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<long>((instrument, value, tags, state) =>
        {
            lock (_counters)
            {
                _counters.TryGetValue(instrument.Name, out var current);
                _counters[instrument.Name] = current + value;

                if (instrument is Histogram<long>)
                {
                    _histogramRecordings.Add((instrument.Name, value));
                }
            }
        });
        _listener.Start();
    }

    /// <inheritdoc />
    public void Dispose() => _listener.Dispose();

    /// <summary>
    /// Verifies that RecordRateLimitRejected increments the counter.
    /// </summary>
    [Fact]
    public void RecordRateLimitRejected_Increments_Counter()
    {
        var before = GetCount("apipipeline.ratelimit.rejected");

        ApiPipelineTelemetry.RecordRateLimitRejected();

        _listener.RecordObservableInstruments();
        var after = GetCount("apipipeline.ratelimit.rejected");
        after.Should().Be(before + 1);
    }

    /// <summary>
    /// Verifies that RecordRateLimitRejected with dimensions increments the counter.
    /// </summary>
    [Fact]
    public void RecordRateLimitRejected_With_Dimensions_Increments_Counter()
    {
        var before = GetCount("apipipeline.ratelimit.rejected");

        ApiPipelineTelemetry.RecordRateLimitRejected("strict", "user");

        _listener.RecordObservableInstruments();
        var after = GetCount("apipipeline.ratelimit.rejected");
        after.Should().Be(before + 1);
    }

    /// <summary>
    /// Verifies that RecordCorrelationIdProcessed increments the counter.
    /// </summary>
    [Fact]
    public void RecordCorrelationIdProcessed_Increments_Counter()
    {
        var before = GetCount("apipipeline.correlation_id.processed");

        ApiPipelineTelemetry.RecordCorrelationIdProcessed();

        _listener.RecordObservableInstruments();
        var after = GetCount("apipipeline.correlation_id.processed");
        after.Should().Be(before + 1);
    }

    /// <summary>
    /// Verifies that RecordExceptionHandled increments the counter.
    /// </summary>
    [Fact]
    public void RecordExceptionHandled_Increments_Counter()
    {
        var before = GetCount("apipipeline.exceptions.handled");

        ApiPipelineTelemetry.RecordExceptionHandled();

        _listener.RecordObservableInstruments();
        var after = GetCount("apipipeline.exceptions.handled");
        after.Should().Be(before + 1);
    }

    /// <summary>
    /// Verifies that RecordCorsRejected increments the counter.
    /// </summary>
    [Fact]
    public void RecordCorsRejected_Increments_Counter()
    {
        var before = GetCount("apipipeline.cors.rejected");

        ApiPipelineTelemetry.RecordCorsRejected();

        _listener.RecordObservableInstruments();
        var after = GetCount("apipipeline.cors.rejected");
        after.Should().Be(before + 1);
    }

    /// <summary>
    /// Verifies that RecordRequestBodyBytes records to the histogram.
    /// </summary>
    [Fact]
    public void RecordRequestBodyBytes_Records_Histogram()
    {
        var countBefore = _histogramRecordings.Count;

        ApiPipelineTelemetry.RecordRequestBodyBytes(4096);

        _histogramRecordings.Count.Should().BeGreaterThan(countBefore);
        _histogramRecordings.Last().Name.Should().Be("apipipeline.request.body_bytes");
        _histogramRecordings.Last().Value.Should().Be(4096);
    }

    /// <summary>
    /// Verifies that RecordDeprecationHeadersAdded increments the counter.
    /// </summary>
    [Fact]
    public void RecordDeprecationHeadersAdded_Increments_Counter()
    {
        var before = GetCount("apipipeline.deprecation.headers_added");

        ApiPipelineTelemetry.RecordDeprecationHeadersAdded("1.0");

        _listener.RecordObservableInstruments();
        var after = GetCount("apipipeline.deprecation.headers_added");
        after.Should().Be(before + 1);
    }

    private long GetCount(string metricName)
    {
        lock (_counters)
        {
            return _counters.TryGetValue(metricName, out var value) ? value : 0;
        }
    }
}
