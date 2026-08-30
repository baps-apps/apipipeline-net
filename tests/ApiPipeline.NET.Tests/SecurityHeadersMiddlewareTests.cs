using System.Net;
using ApiPipeline.NET.Pipeline;
using FluentAssertions;
using Microsoft.AspNetCore.TestHost;

namespace ApiPipeline.NET.Tests;

/// <summary>
/// Tests for <c>UseApiPipelineSecurityHeaders</c>, verifying that API-appropriate security headers
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
        app.UseSimulatedHttps();
        app.UseApiPipeline(pipeline => pipeline.WithSecurityHeaders());
        app.MapGet("/test", () => Results.Ok("ok"));
        await app.StartAsync(TestContext.Current.CancellationToken);

        var client = app.GetApiTestClient();
        var response = await client.GetAsync("/test", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.Contains("X-Content-Type-Options").Should().BeTrue();
        response.Headers.Contains("Referrer-Policy").Should().BeTrue();
        response.Headers.Contains("Strict-Transport-Security").Should().BeTrue();
    }

    /// <summary>
    /// <c>Permissions-Policy</c> is opt-in, and <c>X-Frame-Options</c> can be switched off with
    /// <c>AddXFrameOptions: false</c>.
    /// </summary>
    [Fact]
    public async Task Does_Not_Include_OptIn_Or_Disabled_Headers()
    {
        var config = TestAppBuilder.MinimalConfig(c =>
        {
            c["ApiPipeline:SecurityHeaders:Enabled"] = "true";
            c["ApiPipeline:SecurityHeaders:AddXFrameOptions"] = "false";
        });
        await using var app = await TestAppBuilder.CreateAppAsync(config, Environments.Production);
        app.UseSimulatedHttps();
        app.UseApiPipeline(pipeline => pipeline.WithSecurityHeaders());
        app.MapGet("/test", () => Results.Ok("ok"));
        await app.StartAsync(TestContext.Current.CancellationToken);

        var client = app.GetApiTestClient();
        var response = await client.GetAsync("/test", TestContext.Current.CancellationToken);

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
        app.UseSimulatedHttps();
        app.UseApiPipeline(pipeline => pipeline.WithSecurityHeaders());
        app.MapGet("/test", () => Results.Ok("ok"));
        await app.StartAsync(TestContext.Current.CancellationToken);

        var client = app.GetApiTestClient();
        var response = await client.GetAsync("/test", TestContext.Current.CancellationToken);

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
        prodApp.UseSimulatedHttps();
        prodApp.UseApiPipeline(pipeline => pipeline.WithSecurityHeaders());
        prodApp.MapGet("/test", () => Results.Ok("ok"));
        await prodApp.StartAsync(TestContext.Current.CancellationToken);

        var prodClient = prodApp.GetApiTestClient();
        var prodResponse = await prodClient.GetAsync("/test", TestContext.Current.CancellationToken);
        prodResponse.Headers.Contains("Strict-Transport-Security").Should().BeTrue();

        var hstsValue = prodResponse.Headers.GetValues("Strict-Transport-Security").Single();
        hstsValue.Should().Contain("max-age=31536000");
        hstsValue.Should().Contain("includeSubDomains");

        await using var devApp = await TestAppBuilder.CreateAppAsync(config, Environments.Development);
        devApp.UseSimulatedHttps();
        devApp.UseApiPipeline(pipeline => pipeline.WithSecurityHeaders());
        devApp.MapGet("/test", () => Results.Ok("ok"));
        await devApp.StartAsync(TestContext.Current.CancellationToken);

        var devClient = devApp.GetApiTestClient();
        var devResponse = await devClient.GetAsync("/test", TestContext.Current.CancellationToken);
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
        app.UseSimulatedHttps();
        app.UseApiPipeline(pipeline => pipeline.WithSecurityHeaders());
        app.MapGet("/test", () => Results.Ok("ok"));
        await app.StartAsync(TestContext.Current.CancellationToken);

        var client = app.GetApiTestClient();
        var response = await client.GetAsync("/test", TestContext.Current.CancellationToken);

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
        app.UseSimulatedHttps();
        app.UseApiPipeline(pipeline => pipeline.WithSecurityHeaders());
        app.MapGet("/test", () => Results.Ok("ok"));
        await app.StartAsync(TestContext.Current.CancellationToken);

        var client = app.GetApiTestClient();
        var response = await client.GetAsync("/test", TestContext.Current.CancellationToken);

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

        app.UseSimulatedHttps();
        app.UseApiPipeline(pipeline => pipeline.WithSecurityHeaders());
        app.MapGet("/test", () => Results.Ok("ok"));
        await app.StartAsync(TestContext.Current.CancellationToken);

        var client = app.GetApiTestClient();
        var response = await client.GetAsync("/test", TestContext.Current.CancellationToken);

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
        app.UseSimulatedHttps();
        app.UseApiPipeline(pipeline => pipeline.WithSecurityHeaders());
        app.MapGet("/test", () => Results.Ok("ok"));
        await app.StartAsync(TestContext.Current.CancellationToken);

        var response = await app.GetApiTestClient().GetAsync("/test", TestContext.Current.CancellationToken);

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
            c["ApiPipeline:SecurityHeaders:Enabled"] = "true";
            c["ApiPipeline:SecurityHeaders:ContentSecurityPolicy"] = "default-src 'none'";
        });
        await using var app = await TestAppBuilder.CreateAppAsync(config);
        app.UseSimulatedHttps();
        app.UseApiPipeline(pipeline => pipeline.WithSecurityHeaders());
        app.MapGet("/test", () => Results.Ok("ok"));
        await app.StartAsync(TestContext.Current.CancellationToken);

        var response = await app.GetApiTestClient().GetAsync("/test", TestContext.Current.CancellationToken);

        response.Headers.Contains("Content-Security-Policy").Should().BeTrue();
        response.Headers.GetValues("Content-Security-Policy").Single()
            .Should().Be("default-src 'none'");
    }

    /// <summary>
    /// With no configuration, <c>Content-Security-Policy</c> is the OWASP REST Security Cheat Sheet
    /// value for API responses: nothing may load, and the response may not be framed. An API
    /// response rendered as a document by mistake then cannot run script.
    /// </summary>
    [Fact]
    public async Task SecurityHeaders_Csp_DefaultsToTheOwaspApiPolicy()
    {
        var response = await GetAsync(TestAppBuilder.WithSecurityHeaders());

        response.Headers.GetValues("Content-Security-Policy").Should()
            .ContainSingle().Which.Should().Be("default-src 'none'; frame-ancestors 'none'");
    }

    /// <summary>An empty value sends no <c>Content-Security-Policy</c>, as for <c>CacheControl</c>.</summary>
    [Fact]
    public async Task SecurityHeaders_Csp_EmptyValue_OmitsTheHeader()
    {
        var response = await GetAsync(
            TestAppBuilder.WithSecurityHeaders().Also(c => c["ApiPipeline:SecurityHeaders:ContentSecurityPolicy"] = ""));

        response.Headers.Contains("Content-Security-Policy").Should().BeFalse();
    }

    /// <summary>
    /// An endpoint that serves HTML (Swagger UI, a status page) sets its own policy, which is
    /// never overwritten by the default.
    /// </summary>
    [Fact]
    public async Task SecurityHeaders_Csp_SetByTheEndpoint_IsNotOverwritten()
    {
        var response = await GetAsync(TestAppBuilder.WithSecurityHeaders(), context =>
            context.Response.Headers.ContentSecurityPolicy = "default-src 'self'");

        response.Headers.GetValues("Content-Security-Policy").Should()
            .ContainSingle().Which.Should().Be("default-src 'self'");
    }

    /// <summary>
    /// <c>Cross-Origin-Resource-Policy: same-origin</c> by default (OWASP Secure Headers Project):
    /// another site cannot pull an API response into its page with a no-cors request.
    /// </summary>
    [Fact]
    public async Task SecurityHeaders_Corp_DefaultsToSameOrigin()
    {
        var response = await GetAsync(TestAppBuilder.WithSecurityHeaders());

        response.Headers.GetValues("Cross-Origin-Resource-Policy").Should()
            .ContainSingle().Which.Should().Be("same-origin");
    }

    /// <summary>A looser value can be configured, and an empty value sends no header.</summary>
    [Theory]
    [InlineData("cross-origin", "cross-origin")]
    [InlineData("same-site", "same-site")]
    [InlineData("", null)]
    public async Task SecurityHeaders_Corp_IsConfigurable(string configured, string? expected)
    {
        var response = await GetAsync(
            TestAppBuilder.WithSecurityHeaders().Also(c => c["ApiPipeline:SecurityHeaders:CrossOriginResourcePolicy"] = configured));

        if (expected is null)
        {
            response.Headers.Contains("Cross-Origin-Resource-Policy").Should().BeFalse();
        }
        else
        {
            response.Headers.GetValues("Cross-Origin-Resource-Policy").Should().ContainSingle().Which.Should().Be(expected);
        }
    }

    /// <summary>An endpoint that is meant to be embedded elsewhere sets its own value, which wins.</summary>
    [Fact]
    public async Task SecurityHeaders_Corp_SetByTheEndpoint_IsNotOverwritten()
    {
        var response = await GetAsync(TestAppBuilder.WithSecurityHeaders(), context =>
            context.Response.Headers["Cross-Origin-Resource-Policy"] = "cross-origin");

        response.Headers.GetValues("Cross-Origin-Resource-Policy").Should()
            .ContainSingle().Which.Should().Be("cross-origin");
    }

    /// <summary>A value browsers do not understand (here a typo) fails startup rather than protecting nothing.</summary>
    [Fact]
    public async Task SecurityHeaders_Corp_UnknownValue_FailsStartup()
    {
        await using var app = await TestAppBuilder.CreateAppAsync(
            TestAppBuilder.WithSecurityHeaders().Also(c => c["ApiPipeline:SecurityHeaders:CrossOriginResourcePolicy"] = "same_origin"));

        var start = () => app.StartAsync(TestContext.Current.CancellationToken);

        (await start.Should().ThrowAsync<Microsoft.Extensions.Options.OptionsValidationException>())
            .Which.Message.Should().Contain("CrossOriginResourcePolicy");
    }

    private static async Task<HttpResponseMessage> GetAsync(
        Dictionary<string, string?> config,
        Action<HttpContext>? endpoint = null)
    {
        await using var app = await TestAppBuilder.CreateAppAsync(config);
        app.UseSimulatedHttps();
        app.UseApiPipeline(pipeline => pipeline.WithSecurityHeaders());
        app.MapGet("/test", (HttpContext context) =>
        {
            endpoint?.Invoke(context);
            return Results.Ok("ok");
        });
        await app.StartAsync(TestContext.Current.CancellationToken);

        return await app.GetApiTestClient().GetAsync("/test", TestContext.Current.CancellationToken);
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
            c["ApiPipeline:SecurityHeaders:Enabled"] = "true";
            c["ApiPipeline:SecurityHeaders:EnableStrictTransportSecurity"] = "true";
            c["ApiPipeline:SecurityHeaders:StrictTransportSecurityPreload"] = "true";
        });
        await using var app = await TestAppBuilder.CreateAppAsync(
            config, environment: "Production");
        app.UseSimulatedHttps();
        app.UseApiPipeline(pipeline => pipeline.WithSecurityHeaders());
        app.MapGet("/test", () => Results.Ok("ok"));
        await app.StartAsync(TestContext.Current.CancellationToken);

        var response = await app.GetApiTestClient().GetAsync("/test", TestContext.Current.CancellationToken);

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
            c["ApiPipeline:SecurityHeaders:Enabled"] = "true";
            c["ApiPipeline:SecurityHeaders:PermissionsPolicy"] = "camera=(), microphone=()";
        });
        await using var app = await TestAppBuilder.CreateAppAsync(config);
        app.UseSimulatedHttps();
        app.UseApiPipeline(pipeline => pipeline.WithSecurityHeaders());
        app.MapGet("/test", () => Results.Ok("ok"));
        await app.StartAsync(TestContext.Current.CancellationToken);

        var response = await app.GetApiTestClient().GetAsync("/test", TestContext.Current.CancellationToken);

        response.Headers.Contains("Permissions-Policy").Should().BeTrue();
        response.Headers.GetValues("Permissions-Policy").Single()
            .Should().Be("camera=(), microphone=()");
    }
}
