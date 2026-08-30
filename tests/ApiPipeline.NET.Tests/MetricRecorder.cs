using System.Diagnostics.Metrics;
using ApiPipeline.NET.Observability;

namespace ApiPipeline.NET.Tests;

/// <summary>Records the package's measurements from one <see cref="IMeterFactory"/>, usually a test host's.</summary>
/// <remarks>Filters on <see cref="Meter.Scope"/>, so parallel hosts in the suite are not heard.</remarks>
/// <typeparam name="T">The measurement type.</typeparam>
internal sealed class MetricRecorder<T> : IDisposable
    where T : struct
{
    private readonly MeterListener _listener;
    private readonly List<RecordedMeasurement<T>> _measurements = [];
    private readonly List<Instrument> _instruments = [];

    /// <summary>Records <paramref name="services"/>' meter factory.</summary>
    /// <param name="services">A host's services.</param>
    /// <param name="instrumentName">The one instrument to record, or every instrument when <c>null</c>.</param>
    public MetricRecorder(IServiceProvider services, string? instrumentName = null)
        : this(services.GetRequiredService<IMeterFactory>(), instrumentName)
    {
    }

    /// <summary>Records <paramref name="factory"/>'s instruments.</summary>
    /// <param name="factory">The factory whose meters to record.</param>
    /// <param name="instrumentName">The one instrument to record, or every instrument when <c>null</c>.</param>
    public MetricRecorder(IMeterFactory factory, string? instrumentName = null)
    {
        _listener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == ApiPipelineTelemetry.MeterName
                    && ReferenceEquals(instrument.Meter.Scope, factory)
                    && (instrumentName is null || instrument.Name == instrumentName))
                {
                    lock (_instruments)
                    {
                        _instruments.Add(instrument);
                    }

                    listener.EnableMeasurementEvents(instrument);
                }
            },
        };
        _listener.SetMeasurementEventCallback<T>((instrument, value, tags, _) =>
        {
            lock (_measurements)
            {
                _measurements.Add(new RecordedMeasurement<T>(instrument.Name, value, tags.ToArray()));
            }
        });
        _listener.Start();
    }

    /// <summary>The instruments recorded, in the order they were published.</summary>
    public IReadOnlyList<Instrument> Instruments
    {
        get
        {
            lock (_instruments)
            {
                return [.. _instruments];
            }
        }
    }

    /// <summary>Every measurement so far, in the order recorded.</summary>
    public IReadOnlyList<RecordedMeasurement<T>> Measurements
    {
        get
        {
            lock (_measurements)
            {
                return [.. _measurements];
            }
        }
    }

    /// <summary>The measurements of <paramref name="instrumentName"/> so far.</summary>
    public IReadOnlyList<RecordedMeasurement<T>> For(string instrumentName) =>
        [.. Measurements.Where(m => m.Instrument == instrumentName)];

    /// <inheritdoc />
    public void Dispose() => _listener.Dispose();
}

/// <summary>One recorded measurement.</summary>
/// <typeparam name="T">The measurement type.</typeparam>
/// <param name="Instrument">The instrument's name.</param>
/// <param name="Value">The value.</param>
/// <param name="Tags">The tags, in the order given.</param>
internal sealed record RecordedMeasurement<T>(string Instrument, T Value, KeyValuePair<string, object?>[] Tags)
    where T : struct
{
    /// <summary>The tags by key.</summary>
    public Dictionary<string, object?> TagMap => Tags.ToDictionary(t => t.Key, t => t.Value, StringComparer.Ordinal);
}
