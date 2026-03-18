using System.Net;
using System.Text.Json;
using ApiPipeline.NET.Extensions;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;

namespace ApiPipeline.NET.Tests;

public sealed class RateLimitingTests
{
    [Fact]
    public async Task FixedWindow_Rejects_After_PermitLimit()
    {
        var config = TestAppBuilder.WithRateLimiting(permitLimit: 2, windowSeconds: 60);
        await using var app = await TestAppBuilder.CreateAppAsync(config);
        app.UseRateLimiting();
        app.MapGet("/test", () => Results.Ok("ok"));
        await app.StartAsync();

        var client = app.GetTestClient();

        (await client.GetAsync("/test")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/test")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/test")).StatusCode.Should().Be((HttpStatusCode)429);
    }

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

        await using var app = await TestAppBuilder.CreateAppAsync(config);
        app.UseRateLimiting();
        app.MapGet("/test", () => Results.Ok("ok"));
        await app.StartAsync();

        var client = app.GetTestClient();

        (await client.GetAsync("/test")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/test")).StatusCode.Should().Be((HttpStatusCode)429);
    }

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

        await using var app = await TestAppBuilder.CreateAppAsync(config);
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

    [Fact]
    public async Task RateLimiting_Disabled_Allows_All_Requests()
    {
        var config = TestAppBuilder.MinimalConfig();
        await using var app = await TestAppBuilder.CreateAppAsync(config);
        app.UseRateLimiting();
        app.MapGet("/test", () => Results.Ok("ok"));
        await app.StartAsync();

        var client = app.GetTestClient();

        for (var i = 0; i < 10; i++)
        {
            (await client.GetAsync("/test")).StatusCode.Should().Be(HttpStatusCode.OK);
        }
    }

    [Fact]
    public async Task Rejected_Response_Is_ProblemDetails_Format()
    {
        var config = TestAppBuilder.WithRateLimiting(permitLimit: 1);
        await using var app = await TestAppBuilder.CreateAppAsync(config);
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

        await using var app = await TestAppBuilder.CreateAppAsync(config);
        app.UseRateLimiting();
        app.MapGet("/test", () => Results.Ok("ok"));
        await app.StartAsync();

        var client = app.GetTestClient();

        (await client.GetAsync("/test")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/test")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/test")).StatusCode.Should().Be((HttpStatusCode)429);
    }

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
}
