using System.Net;
using ApiPipeline.NET.Extensions;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;

namespace ApiPipeline.NET.Tests;

/// <summary>
/// Tests for <c>UseSecurityHeaders</c>, verifying that API-appropriate security headers
/// are applied when enabled, browser-only headers are excluded, HSTS is environment-aware,
/// and pre-existing headers are not overwritten.
/// </summary>
public sealed class SecurityHeadersMiddlewareTests
{
    /// <summary>
    /// Verifies that when security headers are enabled, the middleware adds
    /// <c>X-Content-Type-Options</c>, <c>Referrer-Policy</c>, and
    /// <c>Strict-Transport-Security</c> to every response in Production.
    /// </summary>
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

    /// <summary>
    /// Verifies that browser-only headers such as <c>Content-Security-Policy</c>,
    /// <c>X-Frame-Options</c>, and <c>Permissions-Policy</c> are not added by the
    /// middleware, since they are irrelevant or potentially harmful for pure API responses.
    /// </summary>
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

    /// <summary>
    /// Verifies that when security headers are disabled, none of the security-related
    /// headers are added to the response.
    /// </summary>
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

    /// <summary>
    /// Verifies that <c>Strict-Transport-Security</c> (HSTS) is included only in
    /// Production responses, and that the header value contains the required
    /// <c>max-age</c> and <c>includeSubDomains</c> directives.
    /// In Development, HSTS must be absent to avoid locking out local tooling.
    /// </summary>
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

    /// <summary>
    /// Verifies that <c>X-Content-Type-Options</c> is always set to <c>nosniff</c>,
    /// preventing browsers and clients from MIME-sniffing response content away from
    /// the declared content type.
    /// </summary>
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

    /// <summary>
    /// Verifies that <c>Referrer-Policy</c> defaults to <c>no-referrer</c>,
    /// ensuring that no referrer information is sent with outgoing requests.
    /// </summary>
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

    /// <summary>
    /// Verifies that the middleware does not overwrite a security header that was
    /// already set by an earlier middleware in the pipeline, allowing callers to
    /// customise individual header values.
    /// </summary>
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
