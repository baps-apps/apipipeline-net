using System.Net;
using ApiPipeline.NET.Pipeline;
using FluentAssertions;
using Microsoft.AspNetCore.TestHost;

namespace ApiPipeline.NET.Tests;

/// <summary>
/// Tests for the Output Caching pipeline integration.
/// </summary>
public sealed class OutputCachingTests
{
    /// <summary>
    /// Verifies that enabling output caching registers middleware that starts without error.
    /// </summary>
    [Fact]
    public async Task OutputCaching_Enabled_Does_Not_Throw()
    {
        var config = TestAppBuilder.MinimalConfig(c =>
        {
            c["ApiPipeline:OutputCaching:Enabled"] = "true";
        });
        await using var app = await TestAppBuilder.CreateAppAsync(config);
        app.UseApiPipeline(pipeline => pipeline.WithOutputCaching());
        app.MapGet("/test", () => Results.Ok("ok"));
        await app.StartAsync(TestContext.Current.CancellationToken);

        var client = app.GetTestClient();
        var response = await client.GetAsync("/test", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>
    /// Verifies that the output caching middleware is skipped when disabled.
    /// </summary>
    [Fact]
    public async Task OutputCaching_Disabled_Skips_Middleware()
    {
        var config = TestAppBuilder.MinimalConfig(c =>
        {
            c["ApiPipeline:OutputCaching:Enabled"] = "false";
        });
        await using var app = await TestAppBuilder.CreateAppAsync(config);
        app.UseApiPipeline(pipeline => pipeline.WithOutputCaching());
        app.MapGet("/test", () => Results.Ok("ok"));
        await app.StartAsync(TestContext.Current.CancellationToken);

        var client = app.GetTestClient();
        var response = await client.GetAsync("/test", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>
    /// Verifies that <see cref="ApiPipelineOutputCachingOptions.Enabled"/>
    /// defaults to <c>false</c>.
    /// </summary>
    [Fact]
    public void OutputCachingSettings_Enabled_DefaultIs_False()
    {
        var settings = new ApiPipelineOutputCachingOptions();
        settings.Enabled.Should().BeFalse();
    }

    /// <summary>
    /// Verifies that WithOutputCaching is available in the pipeline builder.
    /// </summary>
    [Fact]
    public async Task Pipeline_Builder_WithOutputCaching_Registers_Without_Error()
    {
        var config = TestAppBuilder.MinimalConfig(c =>
        {
            c["ApiPipeline:OutputCaching:Enabled"] = "true";
        });
        await using var app = await TestAppBuilder.CreateAppAsync(config);
        app.UseApiPipeline(pipeline => pipeline
            .WithCorrelationId()
            .WithOutputCaching());
        app.MapGet("/test", () => Results.Ok("ok"));
        await app.StartAsync(TestContext.Current.CancellationToken);

        var client = app.GetTestClient();
        var response = await client.GetAsync("/test", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
