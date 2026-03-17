using System.Net;
using ApiPipeline.NET.Extensions;
using ApiPipeline.NET.Middleware;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ApiPipeline.NET.Tests;

/// <summary>
/// End-to-end smoke tests that verify the full ApiPipeline.NET middleware stack
/// registers, starts, and handles requests without error.
/// </summary>
public sealed class ApiPipelineSmokeTests
{
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
            ["RateLimitingOptions:Enabled"] = "true",
            ["RateLimitingOptions:DefaultPolicy"] = "permissive",
            ["RateLimitingOptions:Policies:0:Name"] = "strict",
            ["RateLimitingOptions:Policies:0:Kind"] = "FixedWindow",
            ["RateLimitingOptions:Policies:0:PermitLimit"] = "5",
            ["RateLimitingOptions:Policies:0:WindowSeconds"] = "60",
            ["RateLimitingOptions:Policies:0:QueueLimit"] = "0",
            ["RateLimitingOptions:Policies:0:QueueProcessingOrder"] = "OldestFirst",
            ["RateLimitingOptions:Policies:1:Name"] = "permissive",
            ["RateLimitingOptions:Policies:1:Kind"] = "FixedWindow",
            ["RateLimitingOptions:Policies:1:PermitLimit"] = "5",
            ["RateLimitingOptions:Policies:1:WindowSeconds"] = "60",
            ["RateLimitingOptions:Policies:1:QueueLimit"] = "0",
            ["RateLimitingOptions:Policies:1:QueueProcessingOrder"] = "OldestFirst",
            ["ResponseCompressionOptions:Enabled"] = "false",
            ["ResponseCachingOptions:Enabled"] = "false",
            ["SecurityHeaders:Enabled"] = "true",
            ["CorsOptions:Enabled"] = "true",
            ["CorsOptions:AllowAllInDevelopment"] = "true",
            ["ApiVersionDeprecationOptions:Enabled"] = "false",
            ["RequestLimitsOptions:Enabled"] = "true",
            ["RequestLimitsOptions:MaxRequestBodySize"] = "1048576",
            ["RequestLimitsOptions:MaxRequestHeadersTotalSize"] = "32768",
            ["RequestLimitsOptions:MaxRequestHeaderCount"] = "100",
            ["RequestLimitsOptions:MaxFormValueCount"] = "1024"
        });

        app.UseCorrelationId();
        app.UseRateLimiting();
        app.UseResponseCompression();
        app.UseResponseCaching();
        app.UseSecurityHeaders();
        app.UseApiVersionDeprecation();
        app.UseCors();
        app.MapGet("/api/ping", () => Results.Ok("pong"));

        await app.StartAsync();

        var client = app.GetTestClient();
        var response = await client.GetAsync("/api/ping");

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
            ["RateLimitingOptions:Enabled"] = "true",
            ["RateLimitingOptions:DefaultPolicy"] = "strict",
            ["RateLimitingOptions:Policies:0:Name"] = "strict",
            ["RateLimitingOptions:Policies:0:Kind"] = "FixedWindow",
            ["RateLimitingOptions:Policies:0:PermitLimit"] = "1",
            ["RateLimitingOptions:Policies:0:WindowSeconds"] = "60",
            ["RateLimitingOptions:Policies:0:QueueLimit"] = "0",
            ["RateLimitingOptions:Policies:0:QueueProcessingOrder"] = "OldestFirst",
            ["RateLimitingOptions:Policies:1:Name"] = "permissive",
            ["RateLimitingOptions:Policies:1:Kind"] = "FixedWindow",
            ["RateLimitingOptions:Policies:1:PermitLimit"] = "100",
            ["RateLimitingOptions:Policies:1:WindowSeconds"] = "60",
            ["RateLimitingOptions:Policies:1:QueueLimit"] = "0",
            ["RateLimitingOptions:Policies:1:QueueProcessingOrder"] = "OldestFirst",
            ["ResponseCompressionOptions:Enabled"] = "false",
            ["ResponseCachingOptions:Enabled"] = "false",
            ["SecurityHeaders:Enabled"] = "false",
            ["CorsOptions:Enabled"] = "false",
            ["ApiVersionDeprecationOptions:Enabled"] = "false"
        });

        app.UseRateLimiting();
        app.MapGet("/api/limited", () => Results.Ok("ok"));

        await app.StartAsync();
        var client = app.GetTestClient();

        (await client.GetAsync("/api/limited")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/api/limited")).StatusCode.Should().Be((HttpStatusCode)429);
    }
}
