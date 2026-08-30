using System.Net;
using ApiPipeline.NET.Pipeline;
using FluentAssertions;
using Microsoft.AspNetCore.TestHost;

namespace ApiPipeline.NET.Tests;

/// <summary>
/// End-to-end smoke tests that verify the full ApiPipeline.NET middleware stack
/// registers, starts, and handles requests without error.
/// </summary>
public sealed class ApiPipelineSmokeTests
{
    /// <summary>
    /// Verifies that the aggregate AddApiPipeline registration path wires the default
    /// service set sufficiently for pipeline startup and correlation-id emission.
    /// </summary>
    [Fact]
    public async Task AddApiPipeline_Registers_Default_ServiceSet()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        var config = TestAppBuilder.MinimalConfig();
        builder.Configuration.AddInMemoryCollection(config);

        builder.Services.AddRouting();
        builder.Services.AddAuthentication(defaultScheme: "Test")
            .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, TestAuthHandler>("Test", _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddApiPipeline(builder.Configuration);

        await using var app = builder.Build();
        app.UseApiPipeline(pipeline => pipeline.WithCorrelationId());
        app.MapGet("/ping", () => Results.Ok("pong"));
        await app.StartAsync(TestContext.Current.CancellationToken);

        var response = await app.GetTestClient().GetAsync("/ping", TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.Contains("X-Correlation-Id").Should().BeTrue();
    }

    /// <summary>
    /// Verifies that all pipeline features can be registered and mounted together,
    /// that the app starts without throwing, and that a successful request receives
    /// both the correlation-id and security headers added by the middleware stack.
    /// </summary>
    [Fact]
    public async Task PerFeatureRegistration_And_Middleware_DoNotThrow_And_AddCorrelationHeader()
    {
        await using var app = await TestAppBuilder.CreateAppAsync(new Dictionary<string, string?>
        {
            ["ApiPipeline:RateLimiting:Enabled"] = "true",
            ["ApiPipeline:RateLimiting:AnonymousFallback"] = "RateLimit",
            ["ApiPipeline:RateLimiting:DefaultPolicy"] = "permissive",
            ["ApiPipeline:RateLimiting:Policies:0:Name"] = "strict",
            ["ApiPipeline:RateLimiting:Policies:0:Kind"] = "FixedWindow",
            ["ApiPipeline:RateLimiting:Policies:0:PermitLimit"] = "5",
            ["ApiPipeline:RateLimiting:Policies:0:Window"] = "00:01:00",
            ["ApiPipeline:RateLimiting:Policies:0:QueueLimit"] = "0",
            ["ApiPipeline:RateLimiting:Policies:0:QueueProcessingOrder"] = "OldestFirst",
            ["ApiPipeline:RateLimiting:Policies:1:Name"] = "permissive",
            ["ApiPipeline:RateLimiting:Policies:1:Kind"] = "FixedWindow",
            ["ApiPipeline:RateLimiting:Policies:1:PermitLimit"] = "5",
            ["ApiPipeline:RateLimiting:Policies:1:Window"] = "00:01:00",
            ["ApiPipeline:RateLimiting:Policies:1:QueueLimit"] = "0",
            ["ApiPipeline:RateLimiting:Policies:1:QueueProcessingOrder"] = "OldestFirst",
            ["ApiPipeline:ResponseCompression:Enabled"] = "false",
            ["ApiPipeline:ResponseCaching:Enabled"] = "false",
            ["ApiPipeline:SecurityHeaders:Enabled"] = "true",
            ["ApiPipeline:Cors:Enabled"] = "true",
            ["ApiPipeline:Cors:AllowAllInDevelopment"] = "true",
            ["ApiPipeline:RequestLimits:Enabled"] = "true",
            ["ApiPipeline:RequestLimits:MaxRequestBodySize"] = "1048576",
            ["ApiPipeline:RequestLimits:MaxRequestHeadersTotalSize"] = "32768",
            ["ApiPipeline:RequestLimits:MaxRequestHeaderCount"] = "100",
            ["ApiPipeline:RequestLimits:MaxFormValueCount"] = "1024"
        }, addProblemDetails: true);

        app.UseApiPipeline(pipeline => pipeline.WithCorrelationId());
        app.UseApiPipeline(pipeline => pipeline.WithRateLimiting());
        app.UseApiPipeline(pipeline => pipeline.WithResponseCompression());
        app.UseApiPipeline(pipeline => pipeline.WithResponseCaching());
        app.UseApiPipeline(pipeline => pipeline.WithSecurityHeaders());
        app.UseApiPipeline(pipeline => pipeline.WithCors());
        app.MapGet("/api/ping", () => Results.Ok("pong"));

        await app.StartAsync(TestContext.Current.CancellationToken);

        var client = app.GetTestClient();
        var response = await client.GetAsync("/api/ping", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.Contains("X-Correlation-Id").Should().BeTrue();
        response.Headers.Contains("X-Content-Type-Options").Should().BeTrue();
    }

    /// <summary>
    /// Verifies that a strict fixed-window rate-limit policy (permit limit of 1)
    /// allows the first request and rejects subsequent requests with HTTP 429.
    /// </summary>
    [Fact]
    public async Task RateLimiting_StrictPolicy_CanReject()
    {
        await using var app = await TestAppBuilder.CreateAppAsync(new Dictionary<string, string?>
        {
            ["ApiPipeline:RateLimiting:Enabled"] = "true",
            ["ApiPipeline:RateLimiting:AnonymousFallback"] = "RateLimit",
            ["ApiPipeline:RateLimiting:DefaultPolicy"] = "strict",
            ["ApiPipeline:RateLimiting:Policies:0:Name"] = "strict",
            ["ApiPipeline:RateLimiting:Policies:0:Kind"] = "FixedWindow",
            ["ApiPipeline:RateLimiting:Policies:0:PermitLimit"] = "1",
            ["ApiPipeline:RateLimiting:Policies:0:Window"] = "00:01:00",
            ["ApiPipeline:RateLimiting:Policies:0:QueueLimit"] = "0",
            ["ApiPipeline:RateLimiting:Policies:0:QueueProcessingOrder"] = "OldestFirst",
            ["ApiPipeline:RateLimiting:Policies:1:Name"] = "permissive",
            ["ApiPipeline:RateLimiting:Policies:1:Kind"] = "FixedWindow",
            ["ApiPipeline:RateLimiting:Policies:1:PermitLimit"] = "100",
            ["ApiPipeline:RateLimiting:Policies:1:Window"] = "00:01:00",
            ["ApiPipeline:RateLimiting:Policies:1:QueueLimit"] = "0",
            ["ApiPipeline:RateLimiting:Policies:1:QueueProcessingOrder"] = "OldestFirst",
            ["ApiPipeline:ResponseCompression:Enabled"] = "false",
            ["ApiPipeline:ResponseCaching:Enabled"] = "false",
            ["ApiPipeline:SecurityHeaders:Enabled"] = "false",
            ["ApiPipeline:Cors:Enabled"] = "false"
        }, addProblemDetails: true);

        app.UseApiPipeline(pipeline => pipeline.WithRateLimiting());
        app.MapGet("/api/limited", () => Results.Ok("ok"));

        await app.StartAsync(TestContext.Current.CancellationToken);
        var client = app.GetTestClient();

        (await client.GetAsync("/api/limited", TestContext.Current.CancellationToken)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/api/limited", TestContext.Current.CancellationToken)).StatusCode.Should().Be((HttpStatusCode)429);
    }
}
