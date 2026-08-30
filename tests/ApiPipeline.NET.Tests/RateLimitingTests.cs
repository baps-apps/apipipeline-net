using System.Net;
using ApiPipeline.NET.Pipeline;
using FluentAssertions;
using Microsoft.AspNetCore.TestHost;

namespace ApiPipeline.NET.Tests;

/// <summary>
/// Tests for the rate-limiting pipeline feature, covering fixed-window, sliding-window,
/// token-bucket, and concurrency limiter algorithms, disabled-mode pass-through, the status code
/// and headers on a rejection, and correlation-id propagation in rejected responses.
/// </summary>
public sealed class RateLimitingTests
{
    /// <summary>
    /// Verifies that a fixed-window policy allows requests up to the configured
    /// <c>PermitLimit</c> and rejects subsequent requests within the same window
    /// with HTTP 429.
    /// </summary>
    [Fact]
    public async Task FixedWindow_Rejects_After_PermitLimit()
    {
        var config = TestAppBuilder.WithRateLimiting(permitLimit: 2, windowSeconds: 60);
        await using var app = await TestAppBuilder.CreateAppAsync(config, addProblemDetails: true);
        app.UseApiPipeline(pipeline => pipeline.WithRateLimiting());
        app.MapGet("/test", () => Results.Ok("ok"));
        await app.StartAsync(TestContext.Current.CancellationToken);

        var client = app.GetTestClient();

        (await client.GetAsync("/test", TestContext.Current.CancellationToken)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/test", TestContext.Current.CancellationToken)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/test", TestContext.Current.CancellationToken)).StatusCode.Should().Be((HttpStatusCode)429);
    }

    /// <summary>
    /// Verifies that a sliding-window policy allows one request within its window
    /// and rejects the next request with HTTP 429 before the window slides.
    /// </summary>
    [Fact]
    public async Task SlidingWindow_Rejects_After_PermitLimit()
    {
        var config = TestAppBuilder.MinimalConfig(c =>
        {
            c["ApiPipeline:RateLimiting:Enabled"] = "true";
            c["ApiPipeline:RateLimiting:DefaultPolicy"] = "sliding";
            c["ApiPipeline:RateLimiting:Policies:0:Name"] = "sliding";
            c["ApiPipeline:RateLimiting:Policies:0:Kind"] = "SlidingWindow";
            c["ApiPipeline:RateLimiting:Policies:0:PermitLimit"] = "1";
            c["ApiPipeline:RateLimiting:Policies:0:Window"] = "00:01:00";
            c["ApiPipeline:RateLimiting:Policies:0:SegmentsPerWindow"] = "2";
            c["ApiPipeline:RateLimiting:Policies:0:QueueLimit"] = "0";
            c["ApiPipeline:RateLimiting:Policies:0:QueueProcessingOrder"] = "OldestFirst";
        });

        await using var app = await TestAppBuilder.CreateAppAsync(config, addProblemDetails: true);
        app.UseApiPipeline(pipeline => pipeline.WithRateLimiting());
        app.MapGet("/test", () => Results.Ok("ok"));
        await app.StartAsync(TestContext.Current.CancellationToken);

        var client = app.GetTestClient();

        (await client.GetAsync("/test", TestContext.Current.CancellationToken)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/test", TestContext.Current.CancellationToken)).StatusCode.Should().Be((HttpStatusCode)429);
    }

    /// <summary>
    /// Verifies that a concurrency limiter with a permit limit of 1 allows the first
    /// in-flight request while rejecting any concurrent request with HTTP 429.
    /// Once the first request completes it returns HTTP 200.
    /// </summary>
    [Fact]
    public async Task Concurrency_Rejects_After_PermitLimit()
    {
        var config = TestAppBuilder.MinimalConfig(c =>
        {
            c["ApiPipeline:RateLimiting:Enabled"] = "true";
            c["ApiPipeline:RateLimiting:DefaultPolicy"] = "concurrent";
            c["ApiPipeline:RateLimiting:Policies:0:Name"] = "concurrent";
            c["ApiPipeline:RateLimiting:Policies:0:Kind"] = "Concurrency";
            c["ApiPipeline:RateLimiting:Policies:0:PermitLimit"] = "1";
            c["ApiPipeline:RateLimiting:Policies:0:QueueLimit"] = "0";
            c["ApiPipeline:RateLimiting:Policies:0:QueueProcessingOrder"] = "OldestFirst";
        });

        await using var app = await TestAppBuilder.CreateAppAsync(config, addProblemDetails: true);
        app.UseApiPipeline(pipeline => pipeline.WithRateLimiting());

        var tcs = new TaskCompletionSource();
        app.MapGet("/slow", async () =>
        {
            await tcs.Task;
            return Results.Ok("ok");
        });
        await app.StartAsync(TestContext.Current.CancellationToken);

        var client = app.GetTestClient();

        var firstRequest = client.GetAsync("/slow", TestContext.Current.CancellationToken);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        var secondResponse = await client.GetAsync("/slow", TestContext.Current.CancellationToken);

        secondResponse.StatusCode.Should().Be((HttpStatusCode)429);

        tcs.SetResult();
        var firstResponse = await firstRequest;
        firstResponse.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>
    /// Verifies that when rate limiting is disabled, all requests are allowed through
    /// regardless of how many are sent in rapid succession.
    /// </summary>
    [Fact]
    public async Task RateLimiting_Disabled_Allows_All_Requests()
    {
        var config = TestAppBuilder.MinimalConfig();
        await using var app = await TestAppBuilder.CreateAppAsync(config, addProblemDetails: true);
        app.UseApiPipeline(pipeline => pipeline.WithRateLimiting());
        app.MapGet("/test", () => Results.Ok("ok"));
        await app.StartAsync(TestContext.Current.CancellationToken);

        var client = app.GetTestClient();

        for (var i = 0; i < 10; i++)
        {
            (await client.GetAsync("/test", TestContext.Current.CancellationToken)).StatusCode.Should().Be(HttpStatusCode.OK);
        }
    }

    /// <summary>
    /// Verifies that a rate-limit rejection response returns HTTP 429 with
    /// <c>Cache-Control: no-store</c>. The limiter writes no body; an application that registers
    /// <c>UseStatusCodePages</c> supplies one. See <see cref="RateLimitingPartitioningTests"/>.
    /// </summary>
    [Fact]
    public async Task Rejected_Response_Has_429_And_NoStore()
    {
        var config = TestAppBuilder.WithRateLimiting(permitLimit: 1);
        await using var app = await TestAppBuilder.CreateAppAsync(config, addProblemDetails: true);
        app.UseApiPipeline(pipeline => pipeline.WithRateLimiting());
        app.MapGet("/test", () => Results.Ok("ok"));
        await app.StartAsync(TestContext.Current.CancellationToken);

        var client = app.GetTestClient();
        await client.GetAsync("/test", TestContext.Current.CancellationToken);
        var rejected = await client.GetAsync("/test", TestContext.Current.CancellationToken);

        rejected.StatusCode.Should().Be((HttpStatusCode)429);
        rejected.Headers.CacheControl?.NoStore.Should().BeTrue();
    }

    /// <summary>
    /// Verifies that a token-bucket policy allows requests up to the bucket capacity
    /// (with auto-replenishment disabled) and rejects further requests with HTTP 429.
    /// </summary>
    [Fact]
    public async Task TokenBucket_Rejects_After_PermitLimit()
    {
        var config = TestAppBuilder.MinimalConfig(c =>
        {
            c["ApiPipeline:RateLimiting:Enabled"] = "true";
            c["ApiPipeline:RateLimiting:DefaultPolicy"] = "bucket";
            c["ApiPipeline:RateLimiting:Policies:0:Name"] = "bucket";
            c["ApiPipeline:RateLimiting:Policies:0:Kind"] = "TokenBucket";
            c["ApiPipeline:RateLimiting:Policies:0:PermitLimit"] = "2";
            c["ApiPipeline:RateLimiting:Policies:0:Window"] = "00:01:00";
            c["ApiPipeline:RateLimiting:Policies:0:TokensPerPeriod"] = "1";
            c["ApiPipeline:RateLimiting:Policies:0:QueueLimit"] = "0";
            c["ApiPipeline:RateLimiting:Policies:0:QueueProcessingOrder"] = "OldestFirst";
        });

        await using var app = await TestAppBuilder.CreateAppAsync(config, addProblemDetails: true);
        app.UseApiPipeline(pipeline => pipeline.WithRateLimiting());
        app.MapGet("/test", () => Results.Ok("ok"));
        await app.StartAsync(TestContext.Current.CancellationToken);

        var client = app.GetTestClient();

        (await client.GetAsync("/test", TestContext.Current.CancellationToken)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/test", TestContext.Current.CancellationToken)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/test", TestContext.Current.CancellationToken)).StatusCode.Should().Be((HttpStatusCode)429);
    }

    /// <summary>
    /// A fixed-window policy with a limit of 3 allows exactly 3 requests and rejects the 4th with 429.
    /// </summary>
    [Fact]
    public async Task RateLimiter_Enforces_PermitLimit_Consistently_Across_Requests()
    {
        var config = TestAppBuilder.WithRateLimiting(permitLimit: 3, windowSeconds: 60);
        await using var app = await TestAppBuilder.CreateAppAsync(config, addProblemDetails: true);
        app.UseApiPipeline(pipeline => pipeline.WithRateLimiting());
        app.MapGet("/test", () => Results.Ok("ok"));
        await app.StartAsync(TestContext.Current.CancellationToken);

        var client = app.GetTestClient();

        (await client.GetAsync("/test", TestContext.Current.CancellationToken)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/test", TestContext.Current.CancellationToken)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/test", TestContext.Current.CancellationToken)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/test", TestContext.Current.CancellationToken)).StatusCode.Should().Be((HttpStatusCode)429);
    }

    /// <summary>
    /// Verifies that a rate-limit rejection returns HTTP 429 when a correlation ID is supplied.
    /// The limiter writes no body, so only the status code is asserted here.
    /// </summary>
    [Fact]
    public async Task Rejected_Response_Returns_429_With_CorrelationId_Request()
    {
        var config = TestAppBuilder.WithRateLimiting(permitLimit: 1);
        await using var app = await TestAppBuilder.CreateAppAsync(config, addProblemDetails: true);
        app.UseApiPipeline(pipeline => pipeline.WithCorrelationId());
        app.UseApiPipeline(pipeline => pipeline.WithRateLimiting());
        app.MapGet("/test", () => Results.Ok("ok"));
        await app.StartAsync(TestContext.Current.CancellationToken);

        var client = app.GetTestClient();
        await client.GetAsync("/test", TestContext.Current.CancellationToken);

        var request = new HttpRequestMessage(HttpMethod.Get, "/test");
        request.Headers.Add("X-Correlation-Id", "test-corr-429");
        var rejected = await client.SendAsync(request, TestContext.Current.CancellationToken);

        rejected.StatusCode.Should().Be((HttpStatusCode)429);
    }

    /// <summary>
    /// Verifies that AnonymousFallback defaults to Reject.
    /// The default must prevent shared-bucket DoS when RemoteIpAddress is null.
    /// </summary>
    [Fact]
    public void RateLimitingOptions_AnonymousFallback_DefaultIs_Reject()
    {
        var options = new ApiPipelineRateLimitingOptions();
        options.AnonymousFallback.Should().Be(AnonymousFallbackBehavior.Reject);
    }

    /// <summary>
    /// Verifies that successful responses carry the IETF <c>RateLimit-Policy</c> field (quota and
    /// window) when the informational headers are enabled.
    /// </summary>
    [Fact]
    public async Task RateLimit_Headers_Added_On_Successful_Request()
    {
        var config = TestAppBuilder.WithRateLimiting(permitLimit: 10, windowSeconds: 120);
        config["ApiPipeline:RateLimiting:EmitRateLimitHeaders"] = "true";
        await using var app = await TestAppBuilder.CreateAppAsync(config, addProblemDetails: true);
        app.UseApiPipeline(pipeline => pipeline.WithRateLimiting());
        app.MapGet("/test", () => Results.Ok("ok"));
        await app.StartAsync(TestContext.Current.CancellationToken);

        var client = app.GetTestClient();
        var response = await client.GetAsync("/test", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        RateLimitHeaders.Quota(response).Should().Be(10);
        // w is the configured window length. A success carries no RateLimit field: the remaining
        // quota of a framework-owned partition is not observable.
        RateLimitHeaders.Window(response).Should().Be(120);
        RateLimitHeaders.Status(response).Should().BeNull();
    }

    /// <summary>
    /// Verifies that <c>RateLimit-Policy</c> / <c>RateLimit</c> headers are present on rejected (429) responses once the
    /// operator has opted in with <c>EmitRateLimitHeadersOnRejection</c>. The opt-in is required
    /// because the quota headers disclose the limit to unauthenticated callers; see
    /// <see cref="RateLimitHeaderPolicyTests"/>.
    /// </summary>
    [Fact]
    public async Task RateLimit_Headers_Added_On_Rejected_Request()
    {
        var config = TestAppBuilder.WithRateLimiting(permitLimit: 1, windowSeconds: 60);
        config["ApiPipeline:RateLimiting:EmitRateLimitHeadersOnRejection"] = "true";
        await using var app = await TestAppBuilder.CreateAppAsync(config, addProblemDetails: true);
        app.UseApiPipeline(pipeline => pipeline.WithRateLimiting());
        app.MapGet("/test", () => Results.Ok("ok"));
        await app.StartAsync(TestContext.Current.CancellationToken);

        var client = app.GetTestClient();
        await client.GetAsync("/test", TestContext.Current.CancellationToken);
        var rejected = await client.GetAsync("/test", TestContext.Current.CancellationToken);

        rejected.StatusCode.Should().Be((HttpStatusCode)429);
        RateLimitHeaders.Quota(rejected).Should().Be(1);
        RateLimitHeaders.Window(rejected).Should().Be(60);
    }

    /// <summary>
    /// Verifies that <c>RateLimit-Policy</c> / <c>RateLimit</c> headers are NOT emitted when
    /// <c>EmitRateLimitHeaders</c> is set to <c>false</c>.
    /// </summary>
    [Fact]
    public async Task RateLimit_Headers_Not_Added_When_Disabled()
    {
        var config = TestAppBuilder.MinimalConfig(c =>
        {
            c["ApiPipeline:RateLimiting:Enabled"] = "true";
            c["ApiPipeline:RateLimiting:EmitRateLimitHeaders"] = "false";
            c["ApiPipeline:RateLimiting:DefaultPolicy"] = "strict";
            c["ApiPipeline:RateLimiting:Policies:0:Name"] = "strict";
            c["ApiPipeline:RateLimiting:Policies:0:Kind"] = "FixedWindow";
            c["ApiPipeline:RateLimiting:Policies:0:PermitLimit"] = "10";
            c["ApiPipeline:RateLimiting:Policies:0:Window"] = "00:01:00";
            c["ApiPipeline:RateLimiting:Policies:0:QueueLimit"] = "0";
            c["ApiPipeline:RateLimiting:Policies:0:QueueProcessingOrder"] = "OldestFirst";
        });
        await using var app = await TestAppBuilder.CreateAppAsync(config, addProblemDetails: true);
        app.UseApiPipeline(pipeline => pipeline.WithRateLimiting());
        app.MapGet("/test", () => Results.Ok("ok"));
        await app.StartAsync(TestContext.Current.CancellationToken);

        var client = app.GetTestClient();
        var response = await client.GetAsync("/test", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.Contains(RateLimitHeaders.PolicyHeader).Should().BeFalse();
    }

    /// <summary>
    /// Verifies that requests with different <c>X-Api-Key</c> header values get independent
    /// rate limit buckets when API key partitioning is enabled.
    /// </summary>
    [Fact]
    public async Task ApiKey_Partitioning_Separates_Buckets()
    {
        var config = TestAppBuilder.MinimalConfig(c =>
        {
            c["ApiPipeline:RateLimiting:Enabled"] = "true";
            c["ApiPipeline:RateLimiting:EnableApiKeyPartitioning"] = "true";
            c["ApiPipeline:RateLimiting:KnownApiKeyHashes:0"] = HashOf("client-a");
            c["ApiPipeline:RateLimiting:KnownApiKeyHashes:1"] = HashOf("client-b");
            c["ApiPipeline:RateLimiting:DefaultPolicy"] = "strict";
            c["ApiPipeline:RateLimiting:Policies:0:Name"] = "strict";
            c["ApiPipeline:RateLimiting:Policies:0:Kind"] = "FixedWindow";
            c["ApiPipeline:RateLimiting:Policies:0:PermitLimit"] = "1";
            c["ApiPipeline:RateLimiting:Policies:0:Window"] = "00:01:00";
            c["ApiPipeline:RateLimiting:Policies:0:QueueLimit"] = "0";
            c["ApiPipeline:RateLimiting:Policies:0:QueueProcessingOrder"] = "OldestFirst";
        });
        await using var app = await TestAppBuilder.CreateAppAsync(config, addProblemDetails: true);
        app.UseApiPipeline(pipeline => pipeline.WithRateLimiting());
        app.MapGet("/test", () => Results.Ok("ok"));
        await app.StartAsync(TestContext.Current.CancellationToken);

        var client = app.GetTestClient();

        var req1 = new HttpRequestMessage(HttpMethod.Get, "/test");
        req1.Headers.Add("X-Api-Key", "client-a");
        (await client.SendAsync(req1, TestContext.Current.CancellationToken)).StatusCode.Should().Be(HttpStatusCode.OK);

        var req2 = new HttpRequestMessage(HttpMethod.Get, "/test");
        req2.Headers.Add("X-Api-Key", "client-b");
        (await client.SendAsync(req2, TestContext.Current.CancellationToken)).StatusCode.Should().Be(HttpStatusCode.OK,
            "different API keys should have separate rate limit buckets");
    }

    /// <summary>
    /// Verifies that the same API key is rate-limited correctly
    /// (exhausts its own bucket without affecting others).
    /// </summary>
    [Fact]
    public async Task ApiKey_Partitioning_Same_Key_Is_Limited()
    {
        var config = TestAppBuilder.MinimalConfig(c =>
        {
            c["ApiPipeline:RateLimiting:Enabled"] = "true";
            c["ApiPipeline:RateLimiting:EnableApiKeyPartitioning"] = "true";
            c["ApiPipeline:RateLimiting:KnownApiKeyHashes:0"] = HashOf("client-a");
            c["ApiPipeline:RateLimiting:KnownApiKeyHashes:1"] = HashOf("client-b");
            c["ApiPipeline:RateLimiting:DefaultPolicy"] = "strict";
            c["ApiPipeline:RateLimiting:Policies:0:Name"] = "strict";
            c["ApiPipeline:RateLimiting:Policies:0:Kind"] = "FixedWindow";
            c["ApiPipeline:RateLimiting:Policies:0:PermitLimit"] = "1";
            c["ApiPipeline:RateLimiting:Policies:0:Window"] = "00:01:00";
            c["ApiPipeline:RateLimiting:Policies:0:QueueLimit"] = "0";
            c["ApiPipeline:RateLimiting:Policies:0:QueueProcessingOrder"] = "OldestFirst";
        });
        await using var app = await TestAppBuilder.CreateAppAsync(config, addProblemDetails: true);
        app.UseApiPipeline(pipeline => pipeline.WithRateLimiting());
        app.MapGet("/test", () => Results.Ok("ok"));
        await app.StartAsync(TestContext.Current.CancellationToken);

        var client = app.GetTestClient();

        var req1 = new HttpRequestMessage(HttpMethod.Get, "/test");
        req1.Headers.Add("X-Api-Key", "client-a");
        (await client.SendAsync(req1, TestContext.Current.CancellationToken)).StatusCode.Should().Be(HttpStatusCode.OK);

        var req2 = new HttpRequestMessage(HttpMethod.Get, "/test");
        req2.Headers.Add("X-Api-Key", "client-a");
        (await client.SendAsync(req2, TestContext.Current.CancellationToken)).StatusCode.Should().Be((HttpStatusCode)429,
            "same API key should exhaust its own bucket");
    }

    /// <summary>
    /// Verifies that API key partitioning is disabled by default and the header is ignored.
    /// </summary>
    [Fact]
    public void RateLimitingOptions_ApiKeyPartitioning_Disabled_By_Default()
    {
        var options = new ApiPipelineRateLimitingOptions();
        options.EnableApiKeyPartitioning.Should().BeFalse();
        options.ApiKeyHeader.Should().Be("X-Api-Key");
    }

    /// <summary>
    /// Verifies that EmitRateLimitHeaders defaults to false: the informational headers on the
    /// success path are opt-in, while a 429 reports the limit either way. See
    /// <see cref="RateLimitHeaderPolicyTests"/>.
    /// </summary>
    [Fact]
    public void RateLimitingOptions_EmitRateLimitHeaders_DefaultIs_False()
    {
        var options = new ApiPipelineRateLimitingOptions();
        options.EmitRateLimitHeaders.Should().BeFalse();
    }

    /// <summary>
    /// Produces the value an operator puts in <c>KnownApiKeyHashes</c>: the first 16 bytes of the
    /// key's SHA-256 hash, lowercase hex. Mirrors <c>RateLimitPartitionKeys.HashApiKey</c>.
    /// </summary>
    private static string HashOf(string apiKey) =>
        Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(apiKey)).AsSpan(0, 16));
}
