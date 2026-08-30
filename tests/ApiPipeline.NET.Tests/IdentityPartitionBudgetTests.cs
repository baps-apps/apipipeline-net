using System.Globalization;
using System.Net;
using ApiPipeline.NET.RateLimiting;
using FluentAssertions;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Options;

namespace ApiPipeline.NET.Tests;

/// <summary>
/// Identity partitions are capped by <c>MaxIdentityPartitions</c>; past it a new caller is limited
/// by its address partition, not one shared bucket.
/// </summary>
public sealed class IdentityPartitionBudgetTests
{
    /// <summary>Test-only: partitions on a header. Real resolvers read validated claims.</summary>
    private sealed class HeaderTenantResolver : IRateLimitPartitionKeyResolver
    {
        public RateLimitPartitionKey? Resolve(HttpContext context, ApiPipelineRateLimitPolicy policy) =>
            context.Request.Headers["X-Tenant"].ToString() is { Length: > 0 } tenant
                ? new RateLimitPartitionKey("tenant", tenant)
                : null;
    }

    private static async Task<WebApplication> StartAsync(int maxIdentityPartitions)
    {
        var config = TestAppBuilder.WithRateLimiting(permitLimit: 1, windowSeconds: 60)
            .Also(c => c["ApiPipeline:RateLimiting:MaxIdentityPartitions"] =
                maxIdentityPartitions.ToString(CultureInfo.InvariantCulture));
        var app = await TestAppBuilder.CreateAppAsync(
            config,
            configureServices: services => services.AddApiPipelineRateLimitPartitionKeyResolver<HeaderTenantResolver>());
        app.UseSimulatedPeerAddress();
        app.UseApiPipeline(pipeline => pipeline.WithRateLimiting());
        app.MapGet("/test", () => Results.Ok("ok"));
        await app.StartAsync(TestContext.Current.CancellationToken);
        return app;
    }

    private static async Task<HttpStatusCode> AsTenantAsync(HttpClient client, string tenant)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/test");
        request.Headers.Add(TestPeerAddress.HeaderName, "203.0.113.1");
        request.Headers.Add("X-Tenant", tenant);
        return (await client.SendAsync(request, TestContext.Current.CancellationToken)).StatusCode;
    }

    [Fact]
    public async Task IdentitiesBeyondTheCap_FallBackToTheirAddressPartition()
    {
        await using var app = await StartAsync(maxIdentityPartitions: 2);
        var client = app.GetTestClient();

        (await AsTenantAsync(client, "a")).Should().Be(HttpStatusCode.OK);
        (await AsTenantAsync(client, "b")).Should().Be(HttpStatusCode.OK);
        (await AsTenantAsync(client, "c")).Should().Be(HttpStatusCode.OK, "past the cap: the address bucket's one permit");
        (await AsTenantAsync(client, "d")).Should().Be((HttpStatusCode)429, "it shares the address bucket with c");
    }

    /// <summary>An identity that already has a partition keeps it at the cap.</summary>
    [Fact]
    public async Task KnownIdentity_KeepsItsPartition_AtTheCap()
    {
        await using var app = await StartAsync(maxIdentityPartitions: 1);
        var client = app.GetTestClient();

        (await AsTenantAsync(client, "a")).Should().Be(HttpStatusCode.OK);
        (await AsTenantAsync(client, "b")).Should().Be(HttpStatusCode.OK, "over the cap, so the address bucket");
        (await AsTenantAsync(client, "a")).Should().Be((HttpStatusCode)429, "a's own bucket, already spent");
    }

    [Fact]
    public async Task Overflow_IsCounted_AsIdentity()
    {
        await using var app = await StartAsync(maxIdentityPartitions: 1);
        using var recorder = new MetricRecorder<long>(app.Services, "apipipeline.rate_limiting.partitions.overflow");
        var client = app.GetTestClient();

        await AsTenantAsync(client, "a");
        await AsTenantAsync(client, "b");

        recorder.Measurements.Should().ContainSingle()
            .Which.TagMap.Should().Contain("apipipeline.rate_limiting.partition_type", "identity");
    }

    [Fact]
    public void MaxIdentityPartitions_DefaultsTo100000()
    {
        new ApiPipelineRateLimitingOptions().MaxIdentityPartitions.Should().Be(100_000);
    }

    [Fact]
    public async Task MaxIdentityPartitions_Zero_FailsValidation()
    {
        var config = TestAppBuilder.WithRateLimiting().Also(c => c["ApiPipeline:RateLimiting:MaxIdentityPartitions"] = "0");
        await using var app = await TestAppBuilder.CreateAppAsync(config);

        var act = () => app.Services.GetRequiredService<IOptions<ApiPipelineRateLimitingOptions>>().Value;

        act.Should().Throw<OptionsValidationException>().WithMessage("*MaxIdentityPartitions*");
    }
}
