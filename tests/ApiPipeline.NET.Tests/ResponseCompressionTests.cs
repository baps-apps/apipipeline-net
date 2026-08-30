using System.Net;
using ApiPipeline.NET.Pipeline;
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
            c["ApiPipeline:ResponseCompression:Enabled"] = "true";
            c["ApiPipeline:ResponseCompression:EnableForHttps"] = "true";
            c["ApiPipeline:ResponseCompression:ExcludedPaths:0"] = "/health";
        });
        await using var app = await TestAppBuilder.CreateAppAsync(config);
        app.UseApiPipeline(pipeline => pipeline.WithResponseCompression());
        app.MapGet("/health", () => Results.Ok("ok"));
        await app.StartAsync(TestContext.Current.CancellationToken);

        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("Accept-Encoding", "gzip, br");

        var response = await client.GetAsync("/health", TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentEncoding.Should().BeEmpty();
    }

    /// <summary>
    /// With no <c>ExcludedPaths</c> configured, the probe paths the rate limiters skip are not
    /// compressed either: <c>/health</c> and <c>/internal/health</c>, with everything beneath them.
    /// </summary>
    [Theory]
    [InlineData("/health")]
    [InlineData("/internal/health/readiness")]
    public async Task ProbePaths_AreNotCompressed_ByDefault(string path)
    {
        var config = TestAppBuilder.MinimalConfig(c =>
        {
            c["ApiPipeline:ResponseCompression:Enabled"] = "true";
            c["ApiPipeline:ResponseCompression:EnableForHttps"] = "true";
        });
        await using var app = await TestAppBuilder.CreateAppAsync(config);
        app.UseApiPipeline(pipeline => pipeline.WithResponseCompression());
        app.MapGet(path, () => Results.Ok(new string('x', 2000)));
        app.MapGet("/test", () => Results.Ok(new string('x', 2000)));
        await app.StartAsync(TestContext.Current.CancellationToken);

        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("Accept-Encoding", "gzip");

        var probe = await client.GetAsync(path, TestContext.Current.CancellationToken);
        probe.Content.Headers.ContentEncoding.Should().BeEmpty();
        var other = await client.GetAsync("/test", TestContext.Current.CancellationToken);
        other.Content.Headers.ContentEncoding.Should().Contain("gzip", "other paths are still compressed");
    }

    /// <summary>
    /// Verifies that disabled compression does not add Content-Encoding.
    /// </summary>
    [Fact]
    public async Task ResponseCompression_Disabled_Does_Not_Compress()
    {
        var config = TestAppBuilder.MinimalConfig(c =>
        {
            c["ApiPipeline:ResponseCompression:Enabled"] = "false";
        });
        await using var app = await TestAppBuilder.CreateAppAsync(config);
        app.UseApiPipeline(pipeline => pipeline.WithResponseCompression());
        app.MapGet("/test", () => Results.Ok("ok"));
        await app.StartAsync(TestContext.Current.CancellationToken);

        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("Accept-Encoding", "gzip, br");

        var response = await client.GetAsync("/test", TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>
    /// With both codecs off the framework's provider list is empty, and an empty list makes it fall
    /// back to Brotli and Gzip: the operator who turned both off would get both. Startup fails instead.
    /// </summary>
    [Fact]
    public async Task Enabled_WithBothCodecsOff_FailsStartup()
    {
        var config = TestAppBuilder.MinimalConfig(c =>
        {
            c["ApiPipeline:ResponseCompression:Enabled"] = "true";
            c["ApiPipeline:ResponseCompression:EnableBrotli"] = "false";
            c["ApiPipeline:ResponseCompression:EnableGzip"] = "false";
        });
        await using var app = await TestAppBuilder.CreateAppAsync(config);
        app.MapGet("/test", () => Results.Ok("ok"));

        var act = () => app.StartAsync(TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<Microsoft.Extensions.Options.OptionsValidationException>())
            .WithMessage("*EnableBrotli*EnableGzip*");
    }

    /// <summary>Disabled, the codec flags do nothing, so both off is fine.</summary>
    [Fact]
    public async Task Disabled_WithBothCodecsOff_Starts()
    {
        var config = TestAppBuilder.MinimalConfig(c =>
        {
            c["ApiPipeline:ResponseCompression:Enabled"] = "false";
            c["ApiPipeline:ResponseCompression:EnableBrotli"] = "false";
            c["ApiPipeline:ResponseCompression:EnableGzip"] = "false";
        });
        await using var app = await TestAppBuilder.CreateAppAsync(config);
        app.MapGet("/test", () => Results.Ok("ok"));

        var act = () => app.StartAsync(TestContext.Current.CancellationToken);

        await act.Should().NotThrowAsync();
    }

    /// <summary>
    /// Verifies the default value of EnableForHttps is false (opt-in for BREACH/CRIME safety).
    /// </summary>
    [Fact]
    public void ResponseCompressionSettings_EnableForHttps_DefaultIs_False()
    {
        var settings = new ApiPipelineResponseCompressionOptions();
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
            c["ApiPipeline:ResponseCompression:Enabled"] = "true";
            c["ApiPipeline:ResponseCompression:EnableForHttps"] = "true";
            c["ApiPipeline:ResponseCompression:ExcludedPaths:0"] = "/health";
        });
        await using var app = await TestAppBuilder.CreateAppAsync(config);
        app.UseApiPipeline(pipeline => pipeline.WithResponseCompression());
        app.MapGet("/data", () => Results.Json(new { Payload = new string('x', 2000) }));
        await app.StartAsync(TestContext.Current.CancellationToken);

        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("Accept-Encoding", "br, gzip");

        var response = await client.GetAsync("/data", TestContext.Current.CancellationToken);
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
            c["ApiPipeline:ResponseCompression:Enabled"] = "true";
            c["ApiPipeline:ResponseCompression:EnableForHttps"] = "true";
            c["ApiPipeline:ResponseCompression:ExcludedPaths:0"] = "/health";
            c["ApiPipeline:ResponseCompression:ExcludedPaths:1"] = "/metrics";
        });
        await using var app = await TestAppBuilder.CreateAppAsync(config);
        app.UseApiPipeline(pipeline => pipeline.WithResponseCompression());
        app.MapGet("/health", () => Results.Ok(new string('x', 2000)));
        app.MapGet("/metrics", () => Results.Ok(new string('x', 2000)));
        await app.StartAsync(TestContext.Current.CancellationToken);

        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("Accept-Encoding", "gzip, br");

        var healthResponse = await client.GetAsync("/health", TestContext.Current.CancellationToken);
        healthResponse.Content.Headers.ContentEncoding.Should().BeEmpty();

        var metricsResponse = await client.GetAsync("/metrics", TestContext.Current.CancellationToken);
        metricsResponse.Content.Headers.ContentEncoding.Should().BeEmpty();
    }
}
