using System.Net;
using System.Threading.RateLimiting;
using ApiPipeline.NET.Pipeline;
using ApiPipeline.NET.RateLimiting;
using FluentAssertions;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace ApiPipeline.NET.Tests;

/// <summary>
/// Tests for the <see cref="IApiPipelineRateLimiterFactory"/> seam: a registered factory must back
/// every partition, and the default in-memory factory must produce the framework limiter for each kind.
/// </summary>
public sealed class RateLimiterFactoryTests
{
    private sealed class RecordingFactory : IApiPipelineRateLimiterFactory
    {
        private readonly InMemoryRateLimiterFactory _inner = new();

        public List<(string PartitionKey, ApiPipelineRateLimitPolicy Policy)> Calls { get; } = [];

        public RateLimiter Create(string partitionKey, ApiPipelineRateLimitPolicy policy)
        {
            lock (Calls)
            {
                Calls.Add((partitionKey, policy));
            }

            return _inner.Create(partitionKey, policy);
        }
    }

    /// <summary>
    /// A factory registered by the application must be asked for the limiter of the default-policy
    /// partition, otherwise a replacement backend (for example a shared store) would never be used.
    /// </summary>
    [Fact]
    public async Task CustomFactory_IsUsedForTheDefaultPolicyPartition()
    {
        var recording = new RecordingFactory();
        var config = TestAppBuilder.WithRateLimiting(permitLimit: 5);
        await using var app = await TestAppBuilder.CreateAppAsync(
            config,
            configureServices: s => s.AddSingleton<IApiPipelineRateLimiterFactory>(recording));
        app.UseApiPipeline(pipeline => pipeline.WithRateLimiting());
        app.MapGet("/test", () => Results.Ok("ok"));
        await app.StartAsync(TestContext.Current.CancellationToken);

        var response = await app.GetTestClient().GetAsync("/test", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        recording.Calls.Should().ContainSingle().Which.Policy.Name.Should().Be("strict");
    }

    /// <summary>
    /// Endpoint-scoped named policies go through the same factory; the partition key carries the
    /// policy signature scope so replicas agree on the key.
    /// </summary>
    [Fact]
    public async Task CustomFactory_IsUsedForNamedPolicies()
    {
        var recording = new RecordingFactory();
        var config = TestAppBuilder.WithRateLimiting(permitLimit: 5);
        config["ApiPipeline:RateLimiting:Policies:1:Name"] = "burst";
        config["ApiPipeline:RateLimiting:Policies:1:Kind"] = "FixedWindow";
        config["ApiPipeline:RateLimiting:Policies:1:PermitLimit"] = "10";
        config["ApiPipeline:RateLimiting:Policies:1:Window"] = "00:01:00";
        config["ApiPipeline:RateLimiting:Policies:1:QueueLimit"] = "0";
        await using var app = await TestAppBuilder.CreateAppAsync(
            config,
            configureServices: s => s.AddSingleton<IApiPipelineRateLimiterFactory>(recording));
        app.UseApiPipeline(pipeline => pipeline.WithRateLimiting());
        app.MapGet("/burst", () => Results.Ok("ok")).RequireRateLimiting("burst");
        await app.StartAsync(TestContext.Current.CancellationToken);

        var response = await app.GetTestClient().GetAsync("/burst", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var call = recording.Calls.Should().ContainSingle().Subject;
        call.Policy.Name.Should().Be("burst");
        call.PartitionKey.Should().Contain("|burst:");
    }

    /// <summary>
    /// The default factory must hand back the framework limiter matching each policy kind, built as
    /// the framework's <c>RateLimitPartition.Get*Limiter</c> helpers build it: never with its own
    /// replenishment timer, because the partitioned limiter's heartbeat replenishes every partition.
    /// </summary>
    [Theory]
    [InlineData(RateLimiterKind.FixedWindow, typeof(FixedWindowRateLimiter))]
    [InlineData(RateLimiterKind.SlidingWindow, typeof(SlidingWindowRateLimiter))]
    [InlineData(RateLimiterKind.TokenBucket, typeof(TokenBucketRateLimiter))]
    [InlineData(RateLimiterKind.Concurrency, typeof(ConcurrencyLimiter))]
    public void InMemoryFactory_ProducesTheFrameworkLimiterForEachKind(RateLimiterKind kind, Type expected)
    {
        var policy = new ApiPipelineRateLimitPolicy
        {
            Name = "p",
            Kind = kind,
            PermitLimit = 3,
            QueueLimit = 0,
            Window = TimeSpan.FromSeconds(1),
            SegmentsPerWindow = 2,
            TokensPerPeriod = 1,
        };

        using var limiter = new InMemoryRateLimiterFactory().Create("key|p:0", policy);

        limiter.Should().BeOfType(expected);
        if (limiter is ReplenishingRateLimiter replenishing)
        {
            replenishing.IsAutoReplenishing.Should().BeFalse("the partitioned limiter's heartbeat replenishes it");
        }
    }

    /// <summary>
    /// End to end, a spent window refills on time: the framework's partitioned limiter replenishes
    /// each partition every 100 ms, with no timer per limiter.
    /// </summary>
    [Fact]
    public async Task InMemoryFactory_WindowRefills()
    {
        var config = TestAppBuilder.WithRateLimiting(permitLimit: 2, windowSeconds: 1);
        await using var app = await TestAppBuilder.CreateAppAsync(config);
        app.UseApiPipeline(pipeline => pipeline.WithRateLimiting());
        app.MapGet("/test", () => Results.Ok("ok"));
        await app.StartAsync(TestContext.Current.CancellationToken);
        var client = app.GetTestClient();

        var exhausted = false;
        for (var i = 0; i < 10 && !exhausted; i++)
        {
            using var spent = await client.GetAsync("/test", TestContext.Current.CancellationToken);
            exhausted = spent.StatusCode == (HttpStatusCode)429;
        }

        exhausted.Should().BeTrue("the limit of 2 is spent");
        await Task.Delay(TimeSpan.FromSeconds(1.5), TestContext.Current.CancellationToken);

        using var refilled = await client.GetAsync("/test", TestContext.Current.CancellationToken);
        refilled.StatusCode.Should().Be(HttpStatusCode.OK, "the window refilled");
    }
}
