using ApiPipeline.NET.Middleware;
using FluentAssertions;

namespace ApiPipeline.NET.Tests;

/// <summary>
/// The per-request logging scope: structured providers read its property, and the console logger
/// (with <c>IncludeScopes</c>) prints its text form. Only the correlation id: the host's activity
/// tracking (<c>ActivityTrackingOptions</c>) already scopes every log line with <c>TraceId</c>, and
/// a second <c>TraceId</c> would be a duplicate attribute wherever scopes are flattened.
/// </summary>
public sealed class CorrelationScopeTests
{
    private static CorrelationScope Scope() =>
        new(new DefaultHttpContext(), "abc123", "0af7651916cd43dd8448eb211c80319c", emitTraceId: true);

    /// <summary>Exactly one property, under the documented name, and nothing else.</summary>
    [Fact]
    public void Properties_AreTheCorrelationIdOnly()
    {
        Scope().Should().Equal([new KeyValuePair<string, object?>("CorrelationId", "abc123")]);
    }

    /// <summary>A readable text form, not a type name.</summary>
    [Fact]
    public void ToString_NamesTheCorrelationId() =>
        Scope().ToString().Should().Be("CorrelationId:abc123");

    /// <summary>Indexing past the property throws, as a list must.</summary>
    [Fact]
    public void Indexer_OutOfRange_Throws()
    {
        var act = () => Scope()[1];

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
