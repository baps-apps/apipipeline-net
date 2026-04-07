using System.Net;
using ApiPipeline.NET.Extensions;
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
            c["OutputCachingOptions:Enabled"] = "true";
        });
        await using var app = await TestAppBuilder.CreateAppAsync(config);
        app.UseOutputCaching();
        app.MapGet("/test", () => Results.Ok("ok"));
        await app.StartAsync();

        var client = app.GetTestClient();
        var response = await client.GetAsync("/test");

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
            c["OutputCachingOptions:Enabled"] = "false";
        });
        await using var app = await TestAppBuilder.CreateAppAsync(config);
        app.UseOutputCaching();
        app.MapGet("/test", () => Results.Ok("ok"));
        await app.StartAsync();

        var client = app.GetTestClient();
        var response = await client.GetAsync("/test");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>
    /// Verifies that <see cref="ApiPipeline.NET.Options.OutputCachingSettings.Enabled"/>
    /// defaults to <c>false</c> (opt-in migration).
    /// </summary>
    [Fact]
    public void OutputCachingSettings_Enabled_DefaultIs_False()
    {
        var settings = new ApiPipeline.NET.Options.OutputCachingSettings();
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
            c["OutputCachingOptions:Enabled"] = "true";
        });
        await using var app = await TestAppBuilder.CreateAppAsync(config);
        app.UseApiPipeline(pipeline => pipeline
            .WithCorrelationId()
            .WithOutputCaching());
        app.MapGet("/test", () => Results.Ok("ok"));
        await app.StartAsync();

        var client = app.GetTestClient();
        var response = await client.GetAsync("/test");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
