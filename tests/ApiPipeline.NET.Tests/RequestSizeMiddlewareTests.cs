using System.Net;
using System.Text;
using ApiPipeline.NET.Extensions;
using ApiPipeline.NET.Middleware;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;

namespace ApiPipeline.NET.Tests;

/// <summary>
/// Tests for RequestSizeMiddleware — verifies it passes requests through without disruption.
/// (Histogram recording is verified indirectly via the middleware not throwing.)
/// </summary>
public sealed class RequestSizeMiddlewareTests
{
    /// <summary>
    /// Verifies that a POST request with a body passes through RequestSizeMiddleware unchanged.
    /// </summary>
    [Fact]
    public async Task RequestSizeMiddleware_PassesThrough_Post_With_Body()
    {
        await using var app = await TestAppBuilder.CreateAppAsync(TestAppBuilder.MinimalConfig());
        app.UseMiddleware<RequestSizeMiddleware>();
        app.MapPost("/data", () => Results.Ok("received"));
        await app.StartAsync();

        var client = app.GetTestClient();
        var content = new StringContent("hello world", Encoding.UTF8, "application/json");
        var response = await client.PostAsync("/data", content);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>
    /// Verifies that a GET request without a body passes through without error.
    /// </summary>
    [Fact]
    public async Task RequestSizeMiddleware_PassesThrough_Get_Without_Body()
    {
        await using var app = await TestAppBuilder.CreateAppAsync(TestAppBuilder.MinimalConfig());
        app.UseMiddleware<RequestSizeMiddleware>();
        app.MapGet("/ping", () => Results.Ok("pong"));
        await app.StartAsync();

        var client = app.GetTestClient();
        var response = await client.GetAsync("/ping");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
