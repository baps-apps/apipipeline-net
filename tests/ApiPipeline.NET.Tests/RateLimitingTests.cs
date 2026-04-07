using System.Net;
using System.Text.Json;
using ApiPipeline.NET.Extensions;
using FluentAssertions;
using Microsoft.AspNetCore.TestHost;

namespace ApiPipeline.NET.Tests;

/// <summary>
/// Tests for the rate-limiting pipeline feature, covering fixed-window, sliding-window,
/// token-bucket, and concurrency limiter algorithms, disabled-mode pass-through,
/// RFC 7807 problem-details rejection responses, and correlation-id propagation in
/// rejected responses.
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
        await using var app = await TestAppBuilder.CreateAppAsync(config, addExceptionHandler: true);
        app.UseRateLimiting();
        app.MapGet("/test", () => Results.Ok("ok"));
        await app.StartAsync();

        var client = app.GetTestClient();

        (await client.GetAsync("/test")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/test")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/test")).StatusCode.Should().Be((HttpStatusCode)429);
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
            c["RateLimitingOptions:Enabled"] = "true";
            c["RateLimitingOptions:DefaultPolicy"] = "sliding";
            c["RateLimitingOptions:Policies:0:Name"] = "sliding";
            c["RateLimitingOptions:Policies:0:Kind"] = "SlidingWindow";
            c["RateLimitingOptions:Policies:0:PermitLimit"] = "1";
            c["RateLimitingOptions:Policies:0:WindowSeconds"] = "60";
            c["RateLimitingOptions:Policies:0:SegmentsPerWindow"] = "2";
            c["RateLimitingOptions:Policies:0:QueueLimit"] = "0";
            c["RateLimitingOptions:Policies:0:QueueProcessingOrder"] = "OldestFirst";
        });

        await using var app = await TestAppBuilder.CreateAppAsync(config, addExceptionHandler: true);
        app.UseRateLimiting();
        app.MapGet("/test", () => Results.Ok("ok"));
        await app.StartAsync();

        var client = app.GetTestClient();

        (await client.GetAsync("/test")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/test")).StatusCode.Should().Be((HttpStatusCode)429);
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
            c["RateLimitingOptions:Enabled"] = "true";
            c["RateLimitingOptions:DefaultPolicy"] = "concurrent";
            c["RateLimitingOptions:Policies:0:Name"] = "concurrent";
            c["RateLimitingOptions:Policies:0:Kind"] = "Concurrency";
            c["RateLimitingOptions:Policies:0:PermitLimit"] = "1";
            c["RateLimitingOptions:Policies:0:QueueLimit"] = "0";
            c["RateLimitingOptions:Policies:0:QueueProcessingOrder"] = "OldestFirst";
        });

        await using var app = await TestAppBuilder.CreateAppAsync(config, addExceptionHandler: true);
        app.UseRateLimiting();

        var tcs = new TaskCompletionSource();
        app.MapGet("/slow", async () =>
        {
            await tcs.Task;
            return Results.Ok("ok");
        });
        await app.StartAsync();

        var client = app.GetTestClient();

        var firstRequest = client.GetAsync("/slow");
        await Task.Delay(100);
        var secondResponse = await client.GetAsync("/slow");

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
        await using var app = await TestAppBuilder.CreateAppAsync(config, addExceptionHandler: true);
        app.UseRateLimiting();
        app.MapGet("/test", () => Results.Ok("ok"));
        await app.StartAsync();

        var client = app.GetTestClient();

        for (var i = 0; i < 10; i++)
        {
            (await client.GetAsync("/test")).StatusCode.Should().Be(HttpStatusCode.OK);
        }
    }

    /// <summary>
    /// Verifies that a rate-limit rejection response uses <c>application/problem+json</c>
    /// content type, includes <c>Cache-Control: no-store</c>, and contains a RFC 7807
    /// problem-details body with <c>status</c>, <c>title</c>, and <c>type</c> fields.
    /// </summary>
    [Fact]
    public async Task Rejected_Response_Is_ProblemDetails_Format()
    {
        var config = TestAppBuilder.WithRateLimiting(permitLimit: 1);
        await using var app = await TestAppBuilder.CreateAppAsync(config, addExceptionHandler: true);
        app.UseRateLimiting();
        app.MapGet("/test", () => Results.Ok("ok"));
        await app.StartAsync();

        var client = app.GetTestClient();
        await client.GetAsync("/test");
        var rejected = await client.GetAsync("/test");

        rejected.StatusCode.Should().Be((HttpStatusCode)429);
        rejected.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
        rejected.Headers.CacheControl?.NoStore.Should().BeTrue();

        var body = await rejected.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(body);
        json.RootElement.GetProperty("status").GetInt32().Should().Be(429);
        json.RootElement.GetProperty("title").GetString().Should().Be("Too Many Requests");
        json.RootElement.TryGetProperty("type", out _).Should().BeTrue();
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
            c["RateLimitingOptions:Enabled"] = "true";
            c["RateLimitingOptions:DefaultPolicy"] = "bucket";
            c["RateLimitingOptions:Policies:0:Name"] = "bucket";
            c["RateLimitingOptions:Policies:0:Kind"] = "TokenBucket";
            c["RateLimitingOptions:Policies:0:PermitLimit"] = "2";
            c["RateLimitingOptions:Policies:0:WindowSeconds"] = "60";
            c["RateLimitingOptions:Policies:0:TokensPerPeriod"] = "1";
            c["RateLimitingOptions:Policies:0:QueueLimit"] = "0";
            c["RateLimitingOptions:Policies:0:QueueProcessingOrder"] = "OldestFirst";
            c["RateLimitingOptions:Policies:0:AutoReplenishment"] = "false";
        });

        await using var app = await TestAppBuilder.CreateAppAsync(config, addExceptionHandler: true);
        app.UseRateLimiting();
        app.MapGet("/test", () => Results.Ok("ok"));
        await app.StartAsync();

        var client = app.GetTestClient();

        (await client.GetAsync("/test")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/test")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/test")).StatusCode.Should().Be((HttpStatusCode)429);
    }

    /// <summary>
    /// Regression guard: ensures IOptionsMonitor doesn't break per-policy enforcement.
    /// Verifies that a fixed-window policy with permit limit 3 allows exactly 3 requests
    /// and rejects the 4th with HTTP 429.
    /// </summary>
    [Fact]
    public async Task RateLimiter_Enforces_PermitLimit_Consistently_Across_Requests()
    {
        // Regression guard: ensures IOptionsMonitor doesn't break per-policy enforcement
        var config = TestAppBuilder.WithRateLimiting(permitLimit: 3, windowSeconds: 60);
        await using var app = await TestAppBuilder.CreateAppAsync(config, addExceptionHandler: true);
        app.UseRateLimiting();
        app.MapGet("/test", () => Results.Ok("ok"));
        await app.StartAsync();

        var client = app.GetTestClient();

        (await client.GetAsync("/test")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/test")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/test")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/test")).StatusCode.Should().Be((HttpStatusCode)429);
    }

    /// <summary>
    /// Verifies that a rate-limit rejection response includes both a <c>correlationId</c>
    /// field matching the value sent in the request's <c>X-Correlation-Id</c> header, and
    /// a non-empty <c>traceId</c> field in the problem-details body.
    /// </summary>
    [Fact]
    public async Task Rejected_Response_Contains_CorrelationId_And_TraceId()
    {
        var config = TestAppBuilder.WithRateLimiting(permitLimit: 1);
        await using var app = await TestAppBuilder.CreateAppAsync(config, addExceptionHandler: true);
        app.UseCorrelationId();
        app.UseRateLimiting();
        app.MapGet("/test", () => Results.Ok("ok"));
        await app.StartAsync();

        var client = app.GetTestClient();
        await client.GetAsync("/test");

        var request = new HttpRequestMessage(HttpMethod.Get, "/test");
        request.Headers.Add("X-Correlation-Id", "test-corr-429");
        var rejected = await client.SendAsync(request);

        rejected.StatusCode.Should().Be((HttpStatusCode)429);
        rejected.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");

        var body = await rejected.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(body);
        json.RootElement.GetProperty("status").GetInt32().Should().Be(429);

        json.RootElement.TryGetProperty("correlationId", out var corrId).Should().BeTrue();
        corrId.GetString().Should().Be("test-corr-429");

        json.RootElement.TryGetProperty("traceId", out var traceId).Should().BeTrue();
        traceId.GetString().Should().NotBeNullOrWhiteSpace();
    }

    /// <summary>
    /// Verifies that AnonymousFallback defaults to Reject.
    /// The default must prevent shared-bucket DoS when RemoteIpAddress is null.
    /// </summary>
    [Fact]
    public void RateLimitingOptions_AnonymousFallback_DefaultIs_Reject()
    {
        var options = new ApiPipeline.NET.Options.RateLimitingOptions();
        options.AnonymousFallback.Should().Be(ApiPipeline.NET.Options.AnonymousFallbackBehavior.Reject);
    }

    /// <summary>
    /// Verifies that successful responses include <c>X-RateLimit-Limit</c> and
    /// <c>X-RateLimit-Reset</c> informational headers when rate limiting is enabled.
    /// </summary>
    [Fact]
    public async Task RateLimit_Headers_Added_On_Successful_Request()
    {
        var config = TestAppBuilder.WithRateLimiting(permitLimit: 10, windowSeconds: 120);
        await using var app = await TestAppBuilder.CreateAppAsync(config, addExceptionHandler: true);
        app.UseRateLimiting();
        app.MapGet("/test", () => Results.Ok("ok"));
        await app.StartAsync();

        var client = app.GetTestClient();
        var response = await client.GetAsync("/test");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.GetValues("X-RateLimit-Limit").Should().ContainSingle("10");
        response.Headers.GetValues("X-RateLimit-Reset").Should().ContainSingle("120");
    }

    /// <summary>
    /// Verifies that <c>X-RateLimit-*</c> headers are also present on rejected (429) responses.
    /// </summary>
    [Fact]
    public async Task RateLimit_Headers_Added_On_Rejected_Request()
    {
        var config = TestAppBuilder.WithRateLimiting(permitLimit: 1, windowSeconds: 60);
        await using var app = await TestAppBuilder.CreateAppAsync(config, addExceptionHandler: true);
        app.UseRateLimiting();
        app.MapGet("/test", () => Results.Ok("ok"));
        await app.StartAsync();

        var client = app.GetTestClient();
        await client.GetAsync("/test");
        var rejected = await client.GetAsync("/test");

        rejected.StatusCode.Should().Be((HttpStatusCode)429);
        rejected.Headers.GetValues("X-RateLimit-Limit").Should().ContainSingle("1");
        rejected.Headers.GetValues("X-RateLimit-Reset").Should().ContainSingle("60");
    }

    /// <summary>
    /// Verifies that <c>X-RateLimit-*</c> headers are NOT emitted when
    /// <c>EmitRateLimitHeaders</c> is set to <c>false</c>.
    /// </summary>
    [Fact]
    public async Task RateLimit_Headers_Not_Added_When_Disabled()
    {
        var config = TestAppBuilder.MinimalConfig(c =>
        {
            c["RateLimitingOptions:Enabled"] = "true";
            c["RateLimitingOptions:EmitRateLimitHeaders"] = "false";
            c["RateLimitingOptions:DefaultPolicy"] = "strict";
            c["RateLimitingOptions:Policies:0:Name"] = "strict";
            c["RateLimitingOptions:Policies:0:Kind"] = "FixedWindow";
            c["RateLimitingOptions:Policies:0:PermitLimit"] = "10";
            c["RateLimitingOptions:Policies:0:WindowSeconds"] = "60";
            c["RateLimitingOptions:Policies:0:QueueLimit"] = "0";
            c["RateLimitingOptions:Policies:0:QueueProcessingOrder"] = "OldestFirst";
        });
        await using var app = await TestAppBuilder.CreateAppAsync(config, addExceptionHandler: true);
        app.UseRateLimiting();
        app.MapGet("/test", () => Results.Ok("ok"));
        await app.StartAsync();

        var client = app.GetTestClient();
        var response = await client.GetAsync("/test");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.Contains("X-RateLimit-Limit").Should().BeFalse();
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
            c["RateLimitingOptions:Enabled"] = "true";
            c["RateLimitingOptions:EnableApiKeyPartitioning"] = "true";
            c["RateLimitingOptions:DefaultPolicy"] = "strict";
            c["RateLimitingOptions:Policies:0:Name"] = "strict";
            c["RateLimitingOptions:Policies:0:Kind"] = "FixedWindow";
            c["RateLimitingOptions:Policies:0:PermitLimit"] = "1";
            c["RateLimitingOptions:Policies:0:WindowSeconds"] = "60";
            c["RateLimitingOptions:Policies:0:QueueLimit"] = "0";
            c["RateLimitingOptions:Policies:0:QueueProcessingOrder"] = "OldestFirst";
        });
        await using var app = await TestAppBuilder.CreateAppAsync(config, addExceptionHandler: true);
        app.UseRateLimiting();
        app.MapGet("/test", () => Results.Ok("ok"));
        await app.StartAsync();

        var client = app.GetTestClient();

        var req1 = new HttpRequestMessage(HttpMethod.Get, "/test");
        req1.Headers.Add("X-Api-Key", "client-a");
        (await client.SendAsync(req1)).StatusCode.Should().Be(HttpStatusCode.OK);

        var req2 = new HttpRequestMessage(HttpMethod.Get, "/test");
        req2.Headers.Add("X-Api-Key", "client-b");
        (await client.SendAsync(req2)).StatusCode.Should().Be(HttpStatusCode.OK,
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
            c["RateLimitingOptions:Enabled"] = "true";
            c["RateLimitingOptions:EnableApiKeyPartitioning"] = "true";
            c["RateLimitingOptions:DefaultPolicy"] = "strict";
            c["RateLimitingOptions:Policies:0:Name"] = "strict";
            c["RateLimitingOptions:Policies:0:Kind"] = "FixedWindow";
            c["RateLimitingOptions:Policies:0:PermitLimit"] = "1";
            c["RateLimitingOptions:Policies:0:WindowSeconds"] = "60";
            c["RateLimitingOptions:Policies:0:QueueLimit"] = "0";
            c["RateLimitingOptions:Policies:0:QueueProcessingOrder"] = "OldestFirst";
        });
        await using var app = await TestAppBuilder.CreateAppAsync(config, addExceptionHandler: true);
        app.UseRateLimiting();
        app.MapGet("/test", () => Results.Ok("ok"));
        await app.StartAsync();

        var client = app.GetTestClient();

        var req1 = new HttpRequestMessage(HttpMethod.Get, "/test");
        req1.Headers.Add("X-Api-Key", "client-a");
        (await client.SendAsync(req1)).StatusCode.Should().Be(HttpStatusCode.OK);

        var req2 = new HttpRequestMessage(HttpMethod.Get, "/test");
        req2.Headers.Add("X-Api-Key", "client-a");
        (await client.SendAsync(req2)).StatusCode.Should().Be((HttpStatusCode)429,
            "same API key should exhaust its own bucket");
    }

    /// <summary>
    /// Verifies that API key partitioning is disabled by default and the header is ignored.
    /// </summary>
    [Fact]
    public void RateLimitingOptions_ApiKeyPartitioning_Disabled_By_Default()
    {
        var options = new ApiPipeline.NET.Options.RateLimitingOptions();
        options.EnableApiKeyPartitioning.Should().BeFalse();
        options.ApiKeyHeader.Should().Be("X-Api-Key");
    }

    /// <summary>
    /// Verifies that EmitRateLimitHeaders defaults to true.
    /// </summary>
    [Fact]
    public void RateLimitingOptions_EmitRateLimitHeaders_DefaultIs_True()
    {
        var options = new ApiPipeline.NET.Options.RateLimitingOptions();
        options.EmitRateLimitHeaders.Should().BeTrue();
    }
}
