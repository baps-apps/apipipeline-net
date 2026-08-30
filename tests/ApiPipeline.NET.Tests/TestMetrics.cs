using System.Diagnostics.Metrics;
using ApiPipeline.NET.Observability;

namespace ApiPipeline.NET.Tests;

/// <summary>
/// An <see cref="ApiPipelineMetrics"/> for tests that construct components directly, on a meter
/// factory of its own. Create one per test (a field of the test class does that, since xUnit
/// creates the class for every test) and record it with <see cref="MetricRecorder{T}"/> over
/// <see cref="Factory"/>: tests run in parallel, so a shared instance would mix their measurements.
/// </summary>
internal sealed class TestMetrics : IDisposable
{
    private readonly ServiceProvider _provider = new ServiceCollection().AddMetrics().BuildServiceProvider();

    /// <summary>Creates the factory and the metrics on it.</summary>
    public TestMetrics()
    {
        Factory = _provider.GetRequiredService<IMeterFactory>();
        Instance = new ApiPipelineMetrics(Factory);
    }

    /// <summary>
    /// For tests that need an <see cref="ApiPipelineMetrics"/> but never read what it records.
    /// Shared, so never listen to it.
    /// </summary>
    public static ApiPipelineMetrics Unobserved { get; } = new TestMetrics().Instance;

    /// <summary>The factory <see cref="Instance"/>'s meter came from.</summary>
    public IMeterFactory Factory { get; }

    /// <summary>The metrics.</summary>
    public ApiPipelineMetrics Instance { get; }

    /// <inheritdoc />
    public void Dispose() => _provider.Dispose();
}
