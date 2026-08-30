using System.Globalization;
using System.Net;
using System.Threading.RateLimiting;
using ApiPipeline.NET.Pipeline;
using ApiPipeline.NET.RateLimiting;
using FluentAssertions;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging.Abstractions;

namespace ApiPipeline.NET.Tests;

/// <summary>
/// <c>MaxIpPartitions</c> caps address partitions, which callers can mint at will: past it, new
/// addresses share one overflow bucket per policy (OWASP API4).
/// </summary>
public sealed class RateLimitPartitionBudgetTests
{
    private static Dictionary<string, string?> MainConfig(int maxIpPartitions) =>
        TestAppBuilder.WithRateLimiting(permitLimit: 1, windowSeconds: 60)
            .Also(c => c["ApiPipeline:RateLimiting:MaxIpPartitions"] = maxIpPartitions.ToString(CultureInfo.InvariantCulture));

    private static Dictionary<string, string?> EdgeConfig(int maxIpPartitions) =>
        TestAppBuilder.MinimalConfig(c =>
        {
            c["ApiPipeline:EdgeRateLimiting:Enabled"] = "true";
            c["ApiPipeline:EdgeRateLimiting:PermitLimit"] = "1";
            c["ApiPipeline:EdgeRateLimiting:Window"] = "00:10:00";
            c["ApiPipeline:EdgeRateLimiting:MaxIpPartitions"] = maxIpPartitions.ToString(CultureInfo.InvariantCulture);
        });

    private static async Task<HttpStatusCode> FromAsync(HttpClient client, string address)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/test");
        request.Headers.Add(TestPeerAddress.HeaderName, address);
        return (await client.SendAsync(request, TestContext.Current.CancellationToken)).StatusCode;
    }

    /// <summary>
    /// With room for two address partitions, two addresses get their own buckets; the third and
    /// fourth share one, so the fourth is throttled although its address never sent a request.
    /// </summary>
    [Fact]
    public async Task MainLimiter_AddressesBeyondTheCap_ShareOneOverflowBucket()
    {
        await using var app = await TestAppBuilder.CreateAppAsync(MainConfig(maxIpPartitions: 2));
        app.UseSimulatedPeerAddress();
        app.UseApiPipeline(pipeline => pipeline.WithRateLimiting());
        app.MapGet("/test", () => Results.Ok("ok"));
        await app.StartAsync(TestContext.Current.CancellationToken);
        var client = app.GetTestClient();

        (await FromAsync(client, "203.0.113.1")).Should().Be(HttpStatusCode.OK);
        (await FromAsync(client, "203.0.113.2")).Should().Be(HttpStatusCode.OK);
        (await FromAsync(client, "203.0.113.3")).Should().Be(HttpStatusCode.OK, "the overflow bucket's one permit");
        (await FromAsync(client, "203.0.113.4")).Should().Be((HttpStatusCode)429, "it shares the overflow bucket");
    }

    /// <summary>The edge limiter, keyed only by address, is bounded the same way.</summary>
    [Fact]
    public async Task EdgeLimiter_AddressesBeyondTheCap_ShareOneOverflowBucket()
    {
        await using var app = await TestAppBuilder.CreateAppAsync(EdgeConfig(maxIpPartitions: 1));
        app.UseSimulatedPeerAddress();
        app.UseApiPipeline(pipeline => pipeline.WithEdgeRateLimiting());
        app.MapGet("/test", () => Results.Ok("ok"));
        await app.StartAsync(TestContext.Current.CancellationToken);
        var client = app.GetTestClient();

        (await FromAsync(client, "203.0.113.1")).Should().Be(HttpStatusCode.OK);
        (await FromAsync(client, "203.0.113.2")).Should().Be(HttpStatusCode.OK, "the overflow bucket's one permit");
        (await FromAsync(client, "203.0.113.3")).Should().Be((HttpStatusCode)429, "it shares the overflow bucket");
    }

    /// <summary>
    /// Requests sent to the overflow bucket are counted, so an operator can tell a flood (or a cap
    /// set too low) from ordinary throttling.
    /// </summary>
    [Fact]
    public async Task Overflow_IsCounted_PerLimiter()
    {
        await using var app = await TestAppBuilder.CreateAppAsync(
            MainConfig(maxIpPartitions: 1).Also(c =>
            {
                c["ApiPipeline:EdgeRateLimiting:Enabled"] = "true";
                c["ApiPipeline:EdgeRateLimiting:MaxIpPartitions"] = "1";
            }));
        using var recorder = new MetricRecorder<long>(app.Services);
        app.UseSimulatedPeerAddress();
        app.UseApiPipeline(pipeline => pipeline.WithEdgeRateLimiting());
        app.UseApiPipeline(pipeline => pipeline.WithRateLimiting());
        app.MapGet("/test", () => Results.Ok("ok"));
        await app.StartAsync(TestContext.Current.CancellationToken);
        var client = app.GetTestClient();

        await FromAsync(client, "203.0.113.1");
        await FromAsync(client, "203.0.113.2");

        recorder.For("apipipeline.rate_limiting.partitions.overflow").Sum(m => m.Value).Should().BeGreaterThan(0);
        recorder.For("apipipeline.edge_rate_limiting.partitions.overflow").Sum(m => m.Value).Should().Be(1);
    }

    /// <summary>A cap of zero would send every caller to one bucket; startup rejects it.</summary>
    [Fact]
    public async Task MaxIpPartitions_Zero_FailsStartup()
    {
        await using var app = await TestAppBuilder.CreateAppAsync(MainConfig(maxIpPartitions: 0));
        app.MapGet("/test", () => Results.Ok("ok"));

        var act = () => app.StartAsync(TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<Microsoft.Extensions.Options.OptionsValidationException>();
    }

    /// <summary>The default leaves room for ordinary traffic.</summary>
    [Fact]
    public void MaxIpPartitions_DefaultsTo100000()
    {
        new ApiPipelineRateLimitingOptions().MaxIpPartitions.Should().Be(100_000);
        new ApiPipelineEdgeRateLimitingOptions().MaxIpPartitions.Should().Be(100_000);
    }

    private static RateLimitPartitionBudget Budget(int max) =>
        new(max, RateLimitPartitionBudget.MainLimiter, metrics: null, NullLogger.Instance);

    /// <summary>A key that already has a partition is always admitted, even at the cap.</summary>
    [Fact]
    public void Budget_AdmitsKnownKeys_AtTheCap()
    {
        var budget = Budget(1);
        using var limiter = budget.Track("a", new ConcurrencyLimiter(new ConcurrencyLimiterOptions { PermitLimit = 1 }));

        budget.Admit("a", "overflow").Should().Be("a");
        budget.Admit("b", "overflow").Should().Be("overflow");
    }

    /// <summary>
    /// The framework disposes a partition's limiter when it goes idle; that frees its slot, so the
    /// cap bounds live partitions rather than every address ever seen.
    /// </summary>
    [Fact]
    public void Budget_DisposingAPartition_FreesItsSlot()
    {
        var budget = Budget(1);
        var limiter = budget.Track("a", new ConcurrencyLimiter(new ConcurrencyLimiterOptions { PermitLimit = 1 }));

        limiter.Dispose();
        limiter.Dispose();

        budget.Count.Should().Be(0, "a second dispose must not release the slot twice");
        budget.Admit("b", "overflow").Should().Be("b");
    }

    /// <summary>
    /// The global and the endpoint limiter can both hold a partition for the same key; the slot is
    /// freed only when both have disposed theirs.
    /// </summary>
    [Fact]
    public void Budget_SameKeyInTwoLimiters_HoldsOneSlotUntilBothDispose()
    {
        var budget = Budget(1);
        var first = budget.Track("a", new ConcurrencyLimiter(new ConcurrencyLimiterOptions { PermitLimit = 1 }));
        using var second = budget.Track("a", new ConcurrencyLimiter(new ConcurrencyLimiterOptions { PermitLimit = 1 }));

        budget.Count.Should().Be(1);
        first.Dispose();

        budget.Admit("a", "overflow").Should().Be("a", "the second limiter still holds the partition");
        budget.Admit("b", "overflow").Should().Be("overflow");
    }

    /// <summary>
    /// The partitioned limiter refills every cached replenishing limiter from its heartbeat. A
    /// wrapper that hid <c>TryReplenish</c> would leave the partition empty for good.
    /// </summary>
    [Fact]
    public void Budget_TrackedReplenishingLimiter_StillReplenishes()
    {
        var budget = Budget(10);
        using var inner = new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions
        {
            PermitLimit = 1,
            Window = TimeSpan.FromTicks(1),
            AutoReplenishment = false,
        });
        using var tracked = budget.Track("a", inner);

        var replenishing = tracked.Should().BeAssignableTo<ReplenishingRateLimiter>().Subject;
        replenishing.AttemptAcquire().IsAcquired.Should().BeTrue();
        replenishing.AttemptAcquire().IsAcquired.Should().BeFalse();

        replenishing.TryReplenish().Should().BeTrue();
        replenishing.AttemptAcquire().IsAcquired.Should().BeTrue();
    }
}
