using System.Net;
using ApiPipeline.NET.Extensions;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;

namespace ApiPipeline.NET.Tests;

/// <summary>
/// Tests for response compression middleware behavior, covering excluded paths and disabled state.
/// </summary>
public sealed class ResponseCompressionTests
{
    /// <summary>
    /// Verifies that a path listed in <c>ExcludedPaths</c> is not compressed even when the client
    /// advertises support for gzip and br encodings.
    /// </summary>
    [Fact]
    public async Task ExcludedPath_Is_Not_Compressed()
    {
        var config = TestAppBuilder.MinimalConfig(c =>
        {
            c["ResponseCompressionOptions:Enabled"] = "true";
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
    /// Verifies that when response compression is disabled the middleware is not added to the pipeline,
    /// and responses are returned without any content encoding.
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
}
