using System.Diagnostics.Metrics;
using ApiPipeline.NET.Observability;
using FluentAssertions;

namespace ApiPipeline.NET.Tests;

/// <summary>
/// Instrument names are the wire contract with the metrics backend: renaming one silently breaks
/// every dashboard and alert built on it. These names are pinned deliberately.
/// </summary>
public sealed class MetricNamingTests : IDisposable
{
    private readonly ServiceProvider _provider = new ServiceCollection().AddMetrics().BuildServiceProvider();
    private readonly MeterListener _listener;
    private readonly List<string> _names = [];
    private readonly IMeterFactory _factory;
    private readonly ApiPipelineMetrics _metrics;

    /// <summary>Captures the names of every instrument this package publishes.</summary>
    public MetricNamingTests()
    {
        _factory = _provider.GetRequiredService<IMeterFactory>();

        // Create the instruments first: MeterListener.Start() publishes only instruments that
        // already exist. A fresh factory, so the names come from one known instance.
        _metrics = new ApiPipelineMetrics(_factory);

        _listener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == ApiPipelineTelemetry.MeterName
                    && ReferenceEquals(instrument.Meter.Scope, _factory))
                {
                    lock (_names)
                    {
                        _names.Add(instrument.Name);
                    }

                    listener.EnableMeasurementEvents(instrument);
                }
            }
        };
        _listener.Start();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _listener.Dispose();
        _provider.Dispose();
    }

    /// <summary>
    /// The request body size histogram lives in the package's own namespace. The OpenTelemetry
    /// semantic-convention name <c>http.server.request.body.size</c> is still development status;
    /// claiming it from a library meter would produce a second, conflicting series the day ASP.NET
    /// Core or OpenTelemetry instrumentation starts emitting it.
    /// </summary>
    [Fact]
    public void RequestBodySize_UsesPackageNamespacedName()
    {
        lock (_names)
        {
            _names.Should().Contain("apipipeline.request.body.size");
            _names.Should().NotContain("http.server.request.body.size",
                "a semantic-convention name belongs to the framework's own instrumentation");
            _names.Should().NotContain("apipipeline.request.body_bytes",
                "the 1.x name, which carried the unit in the name");
        }
    }

    /// <summary>
    /// Package-specific signals with no semantic convention keep the <c>apipipeline.</c> prefix, so
    /// it is obvious which meter owns them.
    /// </summary>
    [Fact]
    public void PackageSpecificInstruments_KeepTheApiPipelinePrefix()
    {
        lock (_names)
        {
            _names.Should().Contain("apipipeline.rate_limiting.rejected");
            _names.Should().NotContain("apipipeline.ratelimit.policy_unresolved",
                "removed in v2.0.0: policy names are read once at startup, so every name resolves");
            _names.Should().Contain("apipipeline.edge_rate_limiting.rejected");
            _names.Should().Contain("apipipeline.cors.rejected");
            _names.Should().Contain("apipipeline.correlation_id.rejected");
            _names.Should().NotContain("apipipeline.configuration.reload_rejected",
                "removed in v2.0.0 with configuration reload: every setting is read once at startup");
            _names.Should().NotContain("apipipeline.deprecation.headers_added",
                "the deprecation counter was removed in v2.0.0: Asp.Versioning writes the headers, " +
                "and counting them cost 278 lines of container surgery and a startup guard");
        }
    }

    /// <summary>
    /// Rate limiting instruments spell it <c>rate_limiting</c>, as ASP.NET Core's
    /// <c>aspnetcore.rate_limiting.*</c> do, never <c>ratelimit</c>.
    /// </summary>
    [Fact]
    public void RateLimitInstruments_FollowTheFrameworkSpelling()
    {
        lock (_names)
        {
            _names.Should().NotContain("apipipeline.ratelimit.rejected");
            _names.Should().NotContain("apipipeline.edge_ratelimit.rejected");
            _names.Should().OnlyContain(name => !name.Contains("ratelimit", StringComparison.Ordinal));
        }
    }
}
