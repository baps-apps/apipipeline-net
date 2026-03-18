using System.Net;
using ApiPipeline.NET.Extensions;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;

namespace ApiPipeline.NET.Tests;

public sealed class CorsTests
{
    [Fact]
    public async Task Cors_Disabled_Does_Not_Add_Headers()
    {
        var config = TestAppBuilder.MinimalConfig();
        await using var app = await TestAppBuilder.CreateAppAsync(config);
        app.UseRouting();
        app.UseCors();
        app.MapGet("/test", () => Results.Ok("ok"));
        await app.StartAsync();

        var client = app.GetTestClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "/test");
        request.Headers.Add("Origin", "https://example.com");

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.Contains("Access-Control-Allow-Origin").Should().BeFalse();
    }

    [Fact]
    public async Task Cors_AllowAll_In_Development_Allows_Any_Origin()
    {
        var config = TestAppBuilder.MinimalConfig(c =>
        {
            c["CorsOptions:Enabled"] = "true";
            c["CorsOptions:AllowAllInDevelopment"] = "true";
        });

        await using var app = await TestAppBuilder.CreateAppAsync(config, Environments.Development);
        app.UseRouting();
        app.UseCors();
        app.MapGet("/test", () => Results.Ok("ok"));
        await app.StartAsync();

        var client = app.GetTestClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "/test");
        request.Headers.Add("Origin", "https://any-origin.test");

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.Contains("Access-Control-Allow-Origin").Should().BeTrue();
    }

    [Fact]
    public async Task Cors_Production_With_Explicit_Origins()
    {
        var config = TestAppBuilder.MinimalConfig(c =>
        {
            c["CorsOptions:Enabled"] = "true";
            c["CorsOptions:AllowAllInDevelopment"] = "false";
            c["CorsOptions:AllowedOrigins:0"] = "https://allowed.example.com";
        });

        await using var app = await TestAppBuilder.CreateAppAsync(config, Environments.Production);
        app.UseRouting();
        app.UseCors();
        app.MapGet("/test", () => Results.Ok("ok"));
        await app.StartAsync();

        var client = app.GetTestClient();

        // Allowed origin
        var allowedRequest = new HttpRequestMessage(HttpMethod.Get, "/test");
        allowedRequest.Headers.Add("Origin", "https://allowed.example.com");
        var allowedResponse = await client.SendAsync(allowedRequest);
        allowedResponse.Headers.Contains("Access-Control-Allow-Origin").Should().BeTrue();

        // Disallowed origin
        var deniedRequest = new HttpRequestMessage(HttpMethod.Get, "/test");
        deniedRequest.Headers.Add("Origin", "https://evil.example.com");
        var deniedResponse = await client.SendAsync(deniedRequest);
        deniedResponse.Headers.Contains("Access-Control-Allow-Origin").Should().BeFalse();
    }

    [Fact]
    public async Task Cors_Preflight_Returns_Correct_Methods()
    {
        var config = TestAppBuilder.MinimalConfig(c =>
        {
            c["CorsOptions:Enabled"] = "true";
            c["CorsOptions:AllowAllInDevelopment"] = "true";
        });

        await using var app = await TestAppBuilder.CreateAppAsync(config, Environments.Development);
        app.UseRouting();
        app.UseCors();
        app.MapGet("/test", () => Results.Ok("ok"));
        await app.StartAsync();

        var client = app.GetTestClient();
        var request = new HttpRequestMessage(HttpMethod.Options, "/test");
        request.Headers.Add("Origin", "https://example.com");
        request.Headers.Add("Access-Control-Request-Method", "POST");

        var response = await client.SendAsync(request);

        response.Headers.Contains("Access-Control-Allow-Origin").Should().BeTrue();
        response.Headers.Contains("Access-Control-Allow-Methods").Should().BeTrue();
    }
}
