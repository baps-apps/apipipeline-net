using FluentAssertions;

namespace ApiPipeline.NET.Tests;

/// <summary>
/// Cardinality contract for the package's counters. Metric dimensions must come from a bounded
/// value space: a caller-controlled string would let one client mint unlimited series, each a
/// permanent allocation in the SDK and in the metrics backend.
/// </summary>
public sealed class TelemetryCardinalityTests : IDisposable
{
    private readonly TestMetrics _metrics = new();
    private readonly MetricRecorder<long> _recorder;

    /// <summary>Records every counter of this test's own metrics.</summary>
    public TelemetryCardinalityTests() => _recorder = new MetricRecorder<long>(_metrics.Factory);

    /// <inheritdoc />
    public void Dispose()
    {
        _recorder.Dispose();
        _metrics.Dispose();
    }

    private Dictionary<string, object?>[] For(string instrument) =>
        [.. _recorder.For(instrument).Select(m => m.TagMap)];

    /// <summary>
    /// The rate-limit rejection counter's two dimensions are both bounded: the policy name comes
    /// from configuration and the partition type from a fixed four-value set.
    /// </summary>
    [Fact]
    public void RateLimitRejected_TagsAreBothBounded()
    {
        _metrics.Instance.RateLimitRejected(policyName: "standard", partitionType: "ip");

        var tags = For("apipipeline.rate_limiting.rejected").Should().ContainSingle().Subject;
        tags.Keys.Should().BeEquivalentTo(["apipipeline.rate_limiting.policy", "apipipeline.rate_limiting.partition_type"]);
        tags["apipipeline.rate_limiting.partition_type"].Should().Be("ip");
    }

    /// <summary>
    /// The edge limiter's rejections have their own instrument, and it carries no tags. The edge
    /// limiter partitions on the client address alone — there is no user, API key, or named policy
    /// before authentication — and the address itself is unbounded, so there is nothing safe to tag.
    /// </summary>
    [Fact]
    public void EdgeRateLimitRejected_HasItsOwnInstrument_AndIsUntagged()
    {
        _metrics.Instance.EdgeRateLimitRejected();

        For("apipipeline.edge_rate_limiting.rejected").Should().ContainSingle()
            .Which.Should().BeEmpty();
        For("apipipeline.rate_limiting.rejected").Should().BeEmpty(
            "the edge path is the one rejection ASP.NET Core cannot see, so it must not share an " +
            "instrument with the framework-covered path");
    }
}
