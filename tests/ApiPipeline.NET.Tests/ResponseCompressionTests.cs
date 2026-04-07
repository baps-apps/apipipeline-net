using System.Net;
using ApiPipeline.NET.Extensions;
using FluentAssertions;
using Microsoft.AspNetCore.TestHost;

namespace ApiPipeline.NET.Tests;

/// <summary>
/// Tests for response compression middleware behavior, covering excluded paths, disabled state,
/// and positive compression verification.
/// </summary>
public sealed class ResponseCompressionTests
{
    /// <summary>
    /// Verifies that a path listed in ExcludedPaths is not compressed.
    /// </summary>
    [Fact]
    public async Task ExcludedPath_Is_Not_Compressed()
    {
        var config = TestAppBuilder.MinimalConfig(c =>
        {
            c["ResponseCompressionOptions:Enabled"] = "true";
            c["ResponseCompressionOptions:EnableForHttps"] = "true";
            c["ResponseCompressionOptions:ExcludedPaths:0"] = "/health";
        });
        await using var app = await TestAppBuilder.CreateAppAsync(config);
        app.UseResponseCompression();
        app.MapGet("/health", () => Results.Ok("ok"));
        await app.StartAsync();

        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("Accept-Encoding", "gzip, br");

        var response = await client.GetAsync("/health");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentEncoding.Should().BeEmpty();
    }

    /// <summary>
    /// Verifies that disabled compression does not add Content-Encoding.
    /// </summary>
    [Fact]
    public async Task ResponseCompression_Disabled_Does_Not_Compress()
    {
        var config = TestAppBuilder.MinimalConfig(c =>
        {
            c["ResponseCompressionOptions:Enabled"] = "false";
        });
        await using var app = await TestAppBuilder.CreateAppAsync(config);
        app.UseResponseCompression();
        app.MapGet("/test", () => Results.Ok("ok"));
        await app.StartAsync();

        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("Accept-Encoding", "gzip, br");

        var response = await client.GetAsync("/test");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>
    /// Verifies the default value of EnableForHttps is false (opt-in for BREACH/CRIME safety).
    /// </summary>
    [Fact]
    public void ResponseCompressionSettings_EnableForHttps_DefaultIs_False()
    {
        var settings = new ApiPipeline.NET.Options.ResponseCompressionSettings();
        settings.EnableForHttps.Should().BeFalse(
            "HTTPS compression must be opt-in to avoid BREACH/CRIME attacks");
    }

    /// <summary>
    /// Verifies that a non-excluded path with compressible content is actually compressed.
    /// </summary>
    [Fact]
    public async Task NonExcludedPath_Is_Compressed()
    {
        var config = TestAppBuilder.MinimalConfig(c =>
        {
            c["ResponseCompressionOptions:Enabled"] = "true";
            c["ResponseCompressionOptions:EnableForHttps"] = "true";
            c["ResponseCompressionOptions:ExcludedPaths:0"] = "/health";
        });
        await using var app = await TestAppBuilder.CreateAppAsync(config);
        app.UseResponseCompression();
        app.MapGet("/data", () => Results.Json(new { Payload = new string('x', 2000) }));
        await app.StartAsync();

        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("Accept-Encoding", "br, gzip");

        var response = await client.GetAsync("/data");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentEncoding.Should().NotBeEmpty(
            "non-excluded paths with compressible content should be compressed");
    }

    /// <summary>
    /// Verifies that multiple excluded paths are all excluded from compression.
    /// </summary>
    [Fact]
    public async Task MultipleExcludedPaths_Are_All_Excluded()
    {
        var config = TestAppBuilder.MinimalConfig(c =>
        {
            c["ResponseCompressionOptions:Enabled"] = "true";
            c["ResponseCompressionOptions:EnableForHttps"] = "true";
            c["ResponseCompressionOptions:ExcludedPaths:0"] = "/health";
            c["ResponseCompressionOptions:ExcludedPaths:1"] = "/metrics";
        });
        await using var app = await TestAppBuilder.CreateAppAsync(config);
        app.UseResponseCompression();
        app.MapGet("/health", () => Results.Ok(new string('x', 2000)));
        app.MapGet("/metrics", () => Results.Ok(new string('x', 2000)));
        await app.StartAsync();

        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("Accept-Encoding", "gzip, br");

        var healthResponse = await client.GetAsync("/health");
        healthResponse.Content.Headers.ContentEncoding.Should().BeEmpty();

        var metricsResponse = await client.GetAsync("/metrics");
        metricsResponse.Content.Headers.ContentEncoding.Should().BeEmpty();
    }
}
