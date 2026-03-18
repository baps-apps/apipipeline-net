using System.Net;
using ApiPipeline.NET.Extensions;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;

namespace ApiPipeline.NET.Tests;

public sealed class SecurityHeadersMiddlewareTests
{
    [Fact]
    public async Task Applies_All_Api_Security_Headers_When_Enabled()
    {
        var config = TestAppBuilder.WithSecurityHeaders(enabled: true);
        await using var app = await TestAppBuilder.CreateAppAsync(config, Environments.Production);
        app.UseSecurityHeaders();
        app.MapGet("/test", () => Results.Ok("ok"));
        await app.StartAsync();

        var client = app.GetTestClient();
        var response = await client.GetAsync("/test");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.Contains("X-Content-Type-Options").Should().BeTrue();
        response.Headers.Contains("Referrer-Policy").Should().BeTrue();
        response.Headers.Contains("Strict-Transport-Security").Should().BeTrue();
    }

    [Fact]
    public async Task Does_Not_Include_Browser_Only_Headers()
    {
        var config = TestAppBuilder.WithSecurityHeaders(enabled: true);
        await using var app = await TestAppBuilder.CreateAppAsync(config, Environments.Production);
        app.UseSecurityHeaders();
        app.MapGet("/test", () => Results.Ok("ok"));
        await app.StartAsync();

        var client = app.GetTestClient();
        var response = await client.GetAsync("/test");

        response.Headers.Contains("Content-Security-Policy").Should().BeFalse();
        response.Headers.Contains("X-Frame-Options").Should().BeFalse();
        response.Headers.Contains("Permissions-Policy").Should().BeFalse();
    }

    [Fact]
    public async Task Skips_Headers_When_Disabled()
    {
        var config = TestAppBuilder.WithSecurityHeaders(enabled: false);
        await using var app = await TestAppBuilder.CreateAppAsync(config);
        app.UseSecurityHeaders();
        app.MapGet("/test", () => Results.Ok("ok"));
        await app.StartAsync();

        var client = app.GetTestClient();
        var response = await client.GetAsync("/test");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.Contains("X-Content-Type-Options").Should().BeFalse();
        response.Headers.Contains("Referrer-Policy").Should().BeFalse();
        response.Headers.Contains("Strict-Transport-Security").Should().BeFalse();
    }

    [Fact]
    public async Task Applies_HSTS_In_Production_Only()
    {
        var config = TestAppBuilder.WithSecurityHeaders(enabled: true);

        await using var prodApp = await TestAppBuilder.CreateAppAsync(config, Environments.Production);
        prodApp.UseSecurityHeaders();
        prodApp.MapGet("/test", () => Results.Ok("ok"));
        await prodApp.StartAsync();

        var prodClient = prodApp.GetTestClient();
        var prodResponse = await prodClient.GetAsync("/test");
        prodResponse.Headers.Contains("Strict-Transport-Security").Should().BeTrue();

        var hstsValue = prodResponse.Headers.GetValues("Strict-Transport-Security").Single();
        hstsValue.Should().Contain("max-age=31536000");
        hstsValue.Should().Contain("includeSubDomains");

        await using var devApp = await TestAppBuilder.CreateAppAsync(config, Environments.Development);
        devApp.UseSecurityHeaders();
        devApp.MapGet("/test", () => Results.Ok("ok"));
        await devApp.StartAsync();

        var devClient = devApp.GetTestClient();
        var devResponse = await devClient.GetAsync("/test");
        devResponse.Headers.Contains("Strict-Transport-Security").Should().BeFalse();
    }

    [Fact]
    public async Task XContentTypeOptions_Defaults_To_Nosniff()
    {
        var config = TestAppBuilder.WithSecurityHeaders(enabled: true);
        await using var app = await TestAppBuilder.CreateAppAsync(config);
        app.UseSecurityHeaders();
        app.MapGet("/test", () => Results.Ok("ok"));
        await app.StartAsync();

        var client = app.GetTestClient();
        var response = await client.GetAsync("/test");

        var value = response.Headers.GetValues("X-Content-Type-Options").Single();
        value.Should().Be("nosniff");
    }

    [Fact]
    public async Task ReferrerPolicy_Defaults_To_NoReferrer()
    {
        var config = TestAppBuilder.WithSecurityHeaders(enabled: true);
        await using var app = await TestAppBuilder.CreateAppAsync(config);
        app.UseSecurityHeaders();
        app.MapGet("/test", () => Results.Ok("ok"));
        await app.StartAsync();

        var client = app.GetTestClient();
        var response = await client.GetAsync("/test");

        var value = response.Headers.GetValues("Referrer-Policy").Single();
        value.Should().Be("no-referrer");
    }

    [Fact]
    public async Task Does_Not_Overwrite_Existing_Headers()
    {
        var config = TestAppBuilder.WithSecurityHeaders(enabled: true);
        await using var app = await TestAppBuilder.CreateAppAsync(config);

        app.Use(async (context, next) =>
        {
            context.Response.Headers["Referrer-Policy"] = "strict-origin";
            await next();
        });

        app.UseSecurityHeaders();
        app.MapGet("/test", () => Results.Ok("ok"));
        await app.StartAsync();

        var client = app.GetTestClient();
        var response = await client.GetAsync("/test");

        var value = response.Headers.GetValues("Referrer-Policy").Single();
        value.Should().Be("strict-origin");
    }
}
