using ApiPipeline.NET.TestSupport;
using FluentAssertions;
using Microsoft.Extensions.Logging;

namespace ApiPipeline.NET.RateLimiting.Redis.Tests;

/// <summary>
/// The Redis package's event IDs (100-199), pinned with their levels; the runbook alerts on 100 and
/// 105. Each must appear in the documented event table at that level.
/// </summary>
public sealed class LogEventIdTests
{
    private static readonly Dictionary<string, (int Id, LogLevel Level)> RedisEvents = new()
    {
        ["SharedRateLimitingUnavailable"] = (100, LogLevel.Warning),
        ["SharedRateLimitingRestored"] = (101, LogLevel.Information),
        ["SharedRateLimitingCallFailed"] = (102, LogLevel.Debug),
        ["SynchronousAttemptsUnanswered"] = (103, LogLevel.Warning),
        ["RedisVersionUnsupported"] = (104, LogLevel.Warning),
        ["RedisEvictsRateLimitKeys"] = (105, LogLevel.Warning),
        ["RedisServerCheckSkipped"] = (106, LogLevel.Debug),
        ["RedisWithoutTls"] = (107, LogLevel.Warning),
    };

    private static Dictionary<string, (int Id, LogLevel Level)> Redis() =>
        LoggerMessageCatalog.Of(typeof(RedisCircuit).Assembly, "ApiPipeline.NET.RateLimiting.Redis.Log");

    /// <summary>Renumbering or re-levelling an event fails here, not in production alerting.</summary>
    [Fact]
    public void RedisEventIds_ArePinned() => Redis().Should().BeEquivalentTo(RedisEvents);

    /// <summary>Every event is in the documented table, at the level it is logged at.</summary>
    [Fact]
    public void EveryEvent_IsDocumented_AtItsLevel()
    {
        var documented = LoggerMessageCatalog.DocumentedEvents();

        foreach (var (name, (id, level)) in Redis())
        {
            documented.Should().ContainKey(id, $"{name} (event {id}) must be in docs/OBSERVABILITY.md");
            documented[id].Should().Be(level.ToString(), $"docs/OBSERVABILITY.md must give {name} (event {id}) its level");
        }
    }
}
