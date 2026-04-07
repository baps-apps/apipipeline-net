using System.Net;
using ApiPipeline.NET.Extensions;
using FluentAssertions;
using Microsoft.AspNetCore.TestHost;

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
    /// Verifies that opt-in headers such as <c>Content-Security-Policy</c> and
    /// <c>Permissions-Policy</c> are not added by default, since they are only emitted
    /// when explicitly configured. <c>X-Frame-Options</c> is now enabled by default
    /// and can be disabled by setting <c>AddXFrameOptions</c> to <c>false</c>.
    /// </summary>
    [Fact]
    public async Task Does_Not_Include_Browser_Only_Headers()
    {
        var config = TestAppBuilder.MinimalConfig(c =>
        {
            c["SecurityHeadersOptions:Enabled"] = "true";
            c["SecurityHeadersOptions:AddXFrameOptions"] = "false";
        });
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

    /// <summary>
    /// Verifies that <c>X-Frame-Options</c> is added with value <c>DENY</c> by default,
    /// protecting against clickjacking attacks.
    /// </summary>
    [Fact]
    public async Task SecurityHeaders_Adds_XFrameOptions_By_Default()
    {
        await using var app = await TestAppBuilder.CreateAppAsync(
            TestAppBuilder.WithSecurityHeaders());
        app.UseSecurityHeaders();
        app.MapGet("/test", () => Results.Ok("ok"));
        await app.StartAsync();

        var response = await app.GetTestClient().GetAsync("/test");

        response.Headers.Contains("X-Frame-Options").Should().BeTrue();
        response.Headers.GetValues("X-Frame-Options").Single().Should().Be("DENY");
    }

    /// <summary>
    /// Verifies that <c>Content-Security-Policy</c> is emitted when explicitly configured,
    /// with the exact policy value provided in settings.
    /// </summary>
    [Fact]
    public async Task SecurityHeaders_Adds_ContentSecurityPolicy_When_Configured()
    {
        var config = TestAppBuilder.MinimalConfig(c =>
        {
            c["SecurityHeadersOptions:Enabled"] = "true";
            c["SecurityHeadersOptions:ContentSecurityPolicy"] = "default-src 'none'";
        });
        await using var app = await TestAppBuilder.CreateAppAsync(config);
        app.UseSecurityHeaders();
        app.MapGet("/test", () => Results.Ok("ok"));
        await app.StartAsync();

        var response = await app.GetTestClient().GetAsync("/test");

        response.Headers.Contains("Content-Security-Policy").Should().BeTrue();
        response.Headers.GetValues("Content-Security-Policy").Single()
            .Should().Be("default-src 'none'");
    }

    /// <summary>
    /// Verifies that <c>Content-Security-Policy</c> is omitted when not configured,
    /// since the default value is <c>null</c>.
    /// </summary>
    [Fact]
    public async Task SecurityHeaders_Does_Not_Add_CSP_When_Not_Configured()
    {
        await using var app = await TestAppBuilder.CreateAppAsync(
            TestAppBuilder.WithSecurityHeaders());
        app.UseSecurityHeaders();
        app.MapGet("/test", () => Results.Ok("ok"));
        await app.StartAsync();

        var response = await app.GetTestClient().GetAsync("/test");

        // CSP is null by default — must not be emitted
        response.Headers.Contains("Content-Security-Policy").Should().BeFalse();
    }

    /// <summary>
    /// Verifies that the <c>preload</c> directive is appended to the HSTS header when
    /// <c>StrictTransportSecurityPreload</c> is set to <c>true</c>.
    /// </summary>
    [Fact]
    public async Task SecurityHeaders_Adds_HSTS_Preload_When_Enabled()
    {
        var config = TestAppBuilder.MinimalConfig(c =>
        {
            c["SecurityHeadersOptions:Enabled"] = "true";
            c["SecurityHeadersOptions:EnableStrictTransportSecurity"] = "true";
            c["SecurityHeadersOptions:StrictTransportSecurityPreload"] = "true";
        });
        await using var app = await TestAppBuilder.CreateAppAsync(
            config, environment: "Production");
        app.UseSecurityHeaders();
        app.MapGet("/test", () => Results.Ok("ok"));
        await app.StartAsync();

        var response = await app.GetTestClient().GetAsync("/test");

        var hsts = response.Headers.GetValues("Strict-Transport-Security").Single();
        hsts.Should().Contain("preload");
    }

    /// <summary>
    /// Verifies that <c>Permissions-Policy</c> is emitted when explicitly configured,
    /// with the exact policy value provided in settings.
    /// </summary>
    [Fact]
    public async Task SecurityHeaders_Adds_PermissionsPolicy_When_Configured()
    {
        var config = TestAppBuilder.MinimalConfig(c =>
        {
            c["SecurityHeadersOptions:Enabled"] = "true";
            c["SecurityHeadersOptions:PermissionsPolicy"] = "camera=(), microphone=()";
        });
        await using var app = await TestAppBuilder.CreateAppAsync(config);
        app.UseSecurityHeaders();
        app.MapGet("/test", () => Results.Ok("ok"));
        await app.StartAsync();

        var response = await app.GetTestClient().GetAsync("/test");

        response.Headers.Contains("Permissions-Policy").Should().BeTrue();
        response.Headers.GetValues("Permissions-Policy").Single()
            .Should().Be("camera=(), microphone=()");
    }
}
