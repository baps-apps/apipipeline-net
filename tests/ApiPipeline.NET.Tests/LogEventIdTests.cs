using ApiPipeline.NET.Pipeline;
using ApiPipeline.NET.TestSupport;
using ApiPipeline.NET.Versioning;
using FluentAssertions;
using Microsoft.Extensions.Logging;

namespace ApiPipeline.NET.Tests;

/// <summary>
/// Event IDs are a contract with operators: <c>docs/OPERATIONS.md</c> alerts on them and
/// <c>docs/OBSERVABILITY.md</c> promises they are stable. Each is pinned here, with its level, and
/// must appear in the documented event table at that level.
/// </summary>
public sealed class LogEventIdTests
{
    private static readonly Dictionary<string, (int Id, LogLevel Level)> CoreEvents = new()
    {
        ["CorrelationIdRejected"] = (1, LogLevel.Debug),
        ["PipelineComposed"] = (10, LogLevel.Information),
        ["PhaseRequestedTwice"] = (11, LogLevel.Warning),
        ["PhaseOrderViolated"] = (12, LogLevel.Warning),
        ["FeatureNotInPipeline"] = (13, LogLevel.Warning),
        ["UseApiPipelineNeverCalled"] = (14, LogLevel.Warning),
        ["PhaseFeatureDisabled"] = (15, LogLevel.Warning),
        ["EdgeRateLimitingEnabled"] = (20, LogLevel.Information),
        ["RateLimitingEnabled"] = (21, LogLevel.Information),
        ["RateLimitingOnSocketTransport"] = (22, LogLevel.Warning),
        ["PartitionKeyResolverFailed"] = (23, LogLevel.Error),
        ["RateLimitPartitionsExhausted"] = (24, LogLevel.Warning),
        ["IdentityPartitionsExhausted"] = (25, LogLevel.Warning),
        ["CompressionEnabledForHttps"] = (30, LogLevel.Warning),
        ["CompressionNotForHttps"] = (31, LogLevel.Information),
        ["CorsAllowAllActive"] = (40, LogLevel.Warning),
        ["CorsDefaultPolicyOverridden"] = (42, LogLevel.Warning),
        ["CorsProviderRegisteredAfter"] = (43, LogLevel.Warning),
        ["CorsCustomProviderWrapped"] = (44, LogLevel.Warning),
        ["ForwardedHeadersMisconfigured"] = (52, LogLevel.Warning),
        ["LegacySectionsFound"] = (62, LogLevel.Warning),
        ["RequestLimitsNotEnforcedByServer"] = (70, LogLevel.Warning),
    };

    private static readonly Dictionary<string, (int Id, LogLevel Level)> VersioningEvents = new()
    {
        ["LegacyVersionDeprecationSection"] = (200, LogLevel.Warning),
        ["VersionReportingOff"] = (201, LogLevel.Warning),
    };

    private static Dictionary<string, (int Id, LogLevel Level)> Core() =>
        LoggerMessageCatalog.Of(typeof(ApiPipelineBuilder).Assembly, "ApiPipeline.NET.Observability.Log");

    private static Dictionary<string, (int Id, LogLevel Level)> Versioning() =>
        LoggerMessageCatalog.Of(typeof(ApiPipelineVersionDeprecationOptions).Assembly, "ApiPipeline.NET.Versioning.Log");

    /// <summary>Renumbering or re-levelling a core event fails here, not in production alerting.</summary>
    [Fact]
    public void CoreEventIds_ArePinned() => Core().Should().BeEquivalentTo(CoreEvents);

    /// <summary>The Versioning package's events, in its 200-299 range.</summary>
    [Fact]
    public void VersioningEventIds_ArePinned() => Versioning().Should().BeEquivalentTo(VersioningEvents);

    /// <summary>Every event is in the documented table, at the level it is logged at.</summary>
    [Fact]
    public void EveryEvent_IsDocumented_AtItsLevel()
    {
        var documented = LoggerMessageCatalog.DocumentedEvents();

        foreach (var (name, (id, level)) in Core().Concat(Versioning()))
        {
            documented.Should().ContainKey(id, $"{name} (event {id}) must be in docs/OBSERVABILITY.md");
            documented[id].Should().Be(level.ToString(), $"docs/OBSERVABILITY.md must give {name} (event {id}) its level");
        }
    }
}
