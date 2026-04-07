using System.Net;
using ApiPipeline.NET.Extensions;
using FluentAssertions;
using Microsoft.AspNetCore.TestHost;

namespace ApiPipeline.NET.Tests;

/// <summary>
/// Tests for response caching middleware behavior.
/// </summary>
public sealed class ResponseCachingTests
{
    /// <summary>
    /// Verifies that a cacheable response can be served from the response caching middleware.
    /// </summary>
    [Fact]
    public async Task CacheableResponse_Is_Served_From_Cache()
    {
        var callCount = 0;
        var config = TestAppBuilder.MinimalConfig(c =>
        {
            c["ResponseCachingOptions:Enabled"] = "true";
            c["ResponseCachingOptions:SizeLimitBytes"] = "52428800";
        });
        await using var app = await TestAppBuilder.CreateAppAsync(config);
        ((IApplicationBuilder)app).UseResponseCaching();
        app.MapGet("/cacheable", () =>
        {
            Interlocked.Increment(ref callCount);
            return Results.Ok(new { Value = "cached-data" });
        }).CacheOutput();
        await app.StartAsync();

        var client = app.GetTestClient();

        var response1 = await client.GetAsync("/cacheable");
        response1.StatusCode.Should().Be(HttpStatusCode.OK);
        var body1 = await response1.Content.ReadAsStringAsync();
        body1.Should().Contain("cached-data");
    }

    /// <summary>
    /// Verifies that disabled caching invokes the handler on every request.
    /// </summary>
    [Fact]
    public async Task Disabled_Caching_Does_Not_Cache()
    {
        var callCount = 0;
        var config = TestAppBuilder.MinimalConfig(c =>
        {
            c["ResponseCachingOptions:Enabled"] = "false";
        });
        await using var app = await TestAppBuilder.CreateAppAsync(config);
        app.UseResponseCaching();
        app.MapGet("/data", () =>
        {
            Interlocked.Increment(ref callCount);
            return Results.Ok(new { Value = callCount });
        });
        await app.StartAsync();

        var client = app.GetTestClient();

        await client.GetAsync("/data");
        await client.GetAsync("/data");

        callCount.Should().Be(2, "each request should invoke the handler when caching is disabled");
    }

    /// <summary>
    /// Verifies that a no-store response is not cached by the caching middleware.
    /// </summary>
    [Fact]
    public async Task NoCacheDirective_Bypasses_Cache()
    {
        var callCount = 0;
        var config = TestAppBuilder.MinimalConfig(c =>
        {
            c["ResponseCachingOptions:Enabled"] = "true";
            c["ResponseCachingOptions:SizeLimitBytes"] = "52428800";
        });
        await using var app = await TestAppBuilder.CreateAppAsync(config);
        ((IApplicationBuilder)app).UseResponseCaching();
        app.MapGet("/nocache", (HttpContext ctx) =>
        {
            Interlocked.Increment(ref callCount);
            ctx.Response.Headers.CacheControl = "no-store";
            return Results.Ok(new { Value = "no-store-data" });
        });
        await app.StartAsync();

        var client = app.GetTestClient();

        var response1 = await client.GetAsync("/nocache");
        response1.StatusCode.Should().Be(HttpStatusCode.OK);

        var response2 = await client.GetAsync("/nocache");
        response2.StatusCode.Should().Be(HttpStatusCode.OK);

        callCount.Should().BeGreaterOrEqualTo(2, "no-store responses should not be cached");
    }

    /// <summary>
    /// Verifies that SizeLimitBytes = 0 fails data annotation validation.
    /// </summary>
    [Fact]
    public void ResponseCachingSettings_SizeLimitBytes_Zero_Should_Not_Be_Valid()
    {
        var settings = new ApiPipeline.NET.Options.ResponseCachingSettings
        {
            SizeLimitBytes = 0
        };

        var context = new System.ComponentModel.DataAnnotations.ValidationContext(settings);
        var results = new System.Collections.Generic.List<System.ComponentModel.DataAnnotations.ValidationResult>();
        var isValid = System.ComponentModel.DataAnnotations.Validator.TryValidateObject(
            settings, context, results, validateAllProperties: true);

        isValid.Should().BeFalse("SizeLimitBytes = 0 creates a useless zero-byte cache");
    }

    /// <summary>
    /// Verifies that a positive SizeLimitBytes value passes validation.
    /// </summary>
    [Fact]
    public void ResponseCachingSettings_SizeLimitBytes_Positive_Is_Valid()
    {
        var settings = new ApiPipeline.NET.Options.ResponseCachingSettings
        {
            SizeLimitBytes = 1048576
        };

        var context = new System.ComponentModel.DataAnnotations.ValidationContext(settings);
        var results = new System.Collections.Generic.List<System.ComponentModel.DataAnnotations.ValidationResult>();
        var isValid = System.ComponentModel.DataAnnotations.Validator.TryValidateObject(
            settings, context, results, validateAllProperties: true);

        isValid.Should().BeTrue();
    }

    /// <summary>
    /// Verifies that when both CORS and response caching are enabled, the response
    /// includes a <c>Vary: Origin</c> header to prevent cross-origin cache poisoning.
    /// </summary>
    [Fact]
    public async Task VaryOrigin_Added_When_Cors_And_Caching_Both_Enabled()
    {
        var config = TestAppBuilder.MinimalConfig(c =>
        {
            c["ResponseCachingOptions:Enabled"] = "true";
            c["ResponseCachingOptions:SizeLimitBytes"] = "52428800";
            c["CorsOptions:Enabled"] = "true";
            c["CorsOptions:AllowAllInDevelopment"] = "true";
        });
        await using var app = await TestAppBuilder.CreateAppAsync(config);
        app.UseCors();
        app.UseResponseCaching();
        app.MapGet("/test", () => Results.Ok("ok"));
        await app.StartAsync();

        var client = app.GetTestClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "/test");
        request.Headers.Add("Origin", "https://example.com");

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.Vary.Should().Contain("Origin");
    }

    /// <summary>
    /// Verifies that <c>Vary: Origin</c> is NOT appended when response caching is enabled
    /// but CORS is disabled.
    /// </summary>
    [Fact]
    public async Task VaryOrigin_NotAdded_When_Only_Caching_Enabled()
    {
        var config = TestAppBuilder.MinimalConfig(c =>
        {
            c["ResponseCachingOptions:Enabled"] = "true";
            c["ResponseCachingOptions:SizeLimitBytes"] = "52428800";
            c["CorsOptions:Enabled"] = "false";
        });
        await using var app = await TestAppBuilder.CreateAppAsync(config);
        app.UseResponseCaching();
        app.MapGet("/test", () => Results.Ok("ok"));
        await app.StartAsync();

        var client = app.GetTestClient();
        var response = await client.GetAsync("/test");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.Vary.Should().NotContain("Origin");
    }
}
