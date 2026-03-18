using System.Net;
using ApiPipeline.NET.Extensions;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;

namespace ApiPipeline.NET.Tests;

public sealed class ForwardedHeadersTests
{
    [Fact]
    public async Task UseApiPipelineForwardedHeaders_Applies_XForwardedFor()
    {
        var config = TestAppBuilder.MinimalConfig();
        await using var app = await TestAppBuilder.CreateAppAsync(config);

        app.UseApiPipelineForwardedHeaders();
        app.MapGet("/ip", (HttpContext ctx) =>
            Results.Ok(new { ip = ctx.Connection.RemoteIpAddress?.ToString() }));
        await app.StartAsync();

        var client = app.GetTestClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "/ip");
        request.Headers.Add("X-Forwarded-For", "203.0.113.42");

        var response = await client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("203.0.113.42");
    }

    [Fact]
    public async Task UseApiPipelineForwardedHeaders_DoesNotThrow_Without_Headers()
    {
        var config = TestAppBuilder.MinimalConfig();
        await using var app = await TestAppBuilder.CreateAppAsync(config);

        app.UseApiPipelineForwardedHeaders();
        app.MapGet("/test", () => Results.Ok("ok"));
        await app.StartAsync();

        var client = app.GetTestClient();
        var response = await client.GetAsync("/test");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task UseApiPipelineForwardedHeaders_Disabled_Does_Not_Process_Headers()
    {
        var config = TestAppBuilder.MinimalConfig(c =>
        {
            c["ForwardedHeadersOptions:Enabled"] = "false";
        });
        await using var app = await TestAppBuilder.CreateAppAsync(config);

        app.UseApiPipelineForwardedHeaders();
        app.MapGet("/ip", (HttpContext ctx) =>
            Results.Ok(new { ip = ctx.Connection.RemoteIpAddress?.ToString() }));
        await app.StartAsync();

        var client = app.GetTestClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "/ip");
        request.Headers.Add("X-Forwarded-For", "203.0.113.42");

        var response = await client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync();
        body.Should().NotContain("203.0.113.42");
    }

    [Fact]
    public async Task UseApiPipelineForwardedHeaders_Respects_ForwardLimit()
    {
        var config = TestAppBuilder.MinimalConfig(c =>
        {
            c["ForwardedHeadersOptions:Enabled"] = "true";
            c["ForwardedHeadersOptions:ForwardLimit"] = "1";
            c["ForwardedHeadersOptions:ClearDefaultProxies"] = "true";
        });
        await using var app = await TestAppBuilder.CreateAppAsync(config);

        app.UseApiPipelineForwardedHeaders();
        app.MapGet("/ip", (HttpContext ctx) =>
            Results.Ok(new { ip = ctx.Connection.RemoteIpAddress?.ToString() }));
        await app.StartAsync();

        var client = app.GetTestClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "/ip");
        request.Headers.Add("X-Forwarded-For", "198.51.100.1, 203.0.113.42");

        var response = await client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("203.0.113.42");
        body.Should().NotContain("198.51.100.1");
    }

    [Fact]
    public async Task UseApiPipelineForwardedHeaders_ClearDefaultProxies_Trusts_All_Listed()
    {
        var config = TestAppBuilder.MinimalConfig(c =>
        {
            c["ForwardedHeadersOptions:Enabled"] = "true";
            c["ForwardedHeadersOptions:ForwardLimit"] = "2";
            c["ForwardedHeadersOptions:ClearDefaultProxies"] = "true";
        });
        await using var app = await TestAppBuilder.CreateAppAsync(config);

        app.UseApiPipelineForwardedHeaders();
        app.MapGet("/ip", (HttpContext ctx) =>
            Results.Ok(new { ip = ctx.Connection.RemoteIpAddress?.ToString() }));
        await app.StartAsync();

        var client = app.GetTestClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "/ip");
        request.Headers.Add("X-Forwarded-For", "198.51.100.1, 203.0.113.42");

        var response = await client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("198.51.100.1");
    }
}
