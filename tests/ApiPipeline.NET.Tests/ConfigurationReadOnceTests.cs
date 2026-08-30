using System.Net;
using ApiPipeline.NET.Pipeline;
using FluentAssertions;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Memory;

namespace ApiPipeline.NET.Tests;

/// <summary>
/// Every setting is read once, at startup, like ASP.NET Core's own middleware options. A
/// configuration change after start (a ConfigMap edit with <c>reloadOnChange</c>) changes nothing
/// until the next restart: no feature switches on or off, no value moves, and an invalid edit cannot
/// break a running process. One rule for every setting, so nobody has to know which ones reload.
/// </summary>
public sealed class ConfigurationReadOnceTests
{
    private static void Reload(WebApplication app, string key, string value)
    {
        var root = (IConfigurationRoot)app.Services.GetRequiredService<IConfiguration>();
        foreach (var provider in root.Providers.OfType<MemoryConfigurationProvider>())
        {
            provider.Set(key, value);
        }

        root.Reload();
    }

    /// <summary>Security headers stay on after a change sets <c>Enabled</c> to false.</summary>
    [Fact]
    public async Task SecurityHeaders_DisabledAfterStartup_StayOn()
    {
        await using var app = await TestAppBuilder.CreateAppAsync(TestAppBuilder.WithSecurityHeaders());
        app.UseApiPipeline(pipeline => pipeline.WithSecurityHeaders());
        app.MapGet("/test", () => "ok");
        await app.StartAsync(TestContext.Current.CancellationToken);
        var client = app.GetTestClient();

        Reload(app, "ApiPipeline:SecurityHeaders:Enabled", "false");
        var response = await client.GetAsync("/test", TestContext.Current.CancellationToken);

        response.Headers.Contains("X-Content-Type-Options").Should().BeTrue();
    }

    /// <summary>A header value changed after startup is not applied until restart.</summary>
    [Fact]
    public async Task SecurityHeaders_ValueChangedAfterStartup_IsNotApplied()
    {
        await using var app = await TestAppBuilder.CreateAppAsync(TestAppBuilder.WithSecurityHeaders());
        app.UseApiPipeline(pipeline => pipeline.WithSecurityHeaders());
        app.MapGet("/test", () => "ok");
        await app.StartAsync(TestContext.Current.CancellationToken);
        var client = app.GetTestClient();

        Reload(app, "ApiPipeline:SecurityHeaders:ReferrerPolicy", "same-origin");
        var response = await client.GetAsync("/test", TestContext.Current.CancellationToken);

        response.Headers.GetValues("Referrer-Policy").Should().ContainSingle().Which.Should().Be("no-referrer");
    }

    /// <summary>
    /// An invalid edit after startup cannot take down a running process: the values read at start
    /// stay in force, and the next restart fails on the invalid value, before serving traffic.
    /// </summary>
    [Fact]
    public async Task InvalidValueAfterStartup_DoesNotBreakRequests()
    {
        await using var app = await TestAppBuilder.CreateAppAsync(TestAppBuilder.WithSecurityHeaders());
        app.UseApiPipeline(pipeline => pipeline.WithCorrelationId());
        app.UseApiPipeline(pipeline => pipeline.WithSecurityHeaders());
        app.MapGet("/test", () => "ok");
        await app.StartAsync(TestContext.Current.CancellationToken);
        var client = app.GetTestClient();

        try
        {
            // Not a valid X-Frame-Options value, and not an int: both fail validation. The
            // framework's own options monitor (built by ValidateOnStart) may throw from the reload
            // callback; nothing the package reads on the request path is affected.
            Reload(app, "ApiPipeline:SecurityHeaders:XFrameOptionsValue", "ALLOW-FROM evil");
            Reload(app, "ApiPipeline:CorrelationId:MaxLength", "not-a-number");
        }
        catch (AggregateException)
        {
        }

        var response = await client.GetAsync("/test", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.GetValues("X-Frame-Options").Should().ContainSingle().Which.Should().Be("DENY");
        response.Headers.Contains("X-Correlation-Id").Should().BeTrue();
    }

    /// <summary>Edge rate limiting keeps limiting after a change sets <c>Enabled</c> to false.</summary>
    [Fact]
    public async Task EdgeRateLimiting_DisabledAfterStartup_KeepsLimiting()
    {
        await using var app = await TestAppBuilder.CreateAppAsync(TestAppBuilder.MinimalConfig(c =>
        {
            c["ApiPipeline:EdgeRateLimiting:Enabled"] = "true";
            c["ApiPipeline:EdgeRateLimiting:PermitLimit"] = "1";
            c["ApiPipeline:EdgeRateLimiting:Window"] = "00:10:00";
        }));
        app.Use(async (ctx, next) =>
        {
            ctx.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.40");
            await next(ctx);
        });
        app.UseApiPipeline(pipeline => pipeline.WithEdgeRateLimiting());
        app.MapGet("/test", () => "ok");
        await app.StartAsync(TestContext.Current.CancellationToken);
        var client = app.GetTestClient();

        (await client.GetAsync("/test", TestContext.Current.CancellationToken)).StatusCode.Should().Be(HttpStatusCode.OK);
        Reload(app, "ApiPipeline:EdgeRateLimiting:Enabled", "false");
        Reload(app, "ApiPipeline:EdgeRateLimiting:PermitLimit", "100");

        (await client.GetAsync("/test", TestContext.Current.CancellationToken)).StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
    }

    /// <summary>A raised <c>PermitLimit</c> applies on the next restart, not before.</summary>
    [Fact]
    public async Task RateLimiting_PermitLimitRaisedAfterStartup_IsNotApplied()
    {
        await using var app = await TestAppBuilder.CreateAppAsync(TestAppBuilder.MinimalConfig(c =>
        {
            c["ApiPipeline:RateLimiting:Enabled"] = "true";
            c["ApiPipeline:RateLimiting:AnonymousFallback"] = "RateLimit";
            c["ApiPipeline:RateLimiting:DefaultPolicy"] = "strict";
            c["ApiPipeline:RateLimiting:Policies:0:Name"] = "strict";
            c["ApiPipeline:RateLimiting:Policies:0:PermitLimit"] = "2";
            c["ApiPipeline:RateLimiting:Policies:0:Window"] = "00:10:00";
        }));
        app.UseApiPipeline(pipeline => pipeline.WithRateLimiting());
        app.MapGet("/test", () => Results.Ok("ok"));
        await app.StartAsync(TestContext.Current.CancellationToken);
        var client = app.GetTestClient();

        (await client.GetAsync("/test", TestContext.Current.CancellationToken)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/test", TestContext.Current.CancellationToken)).StatusCode.Should().Be(HttpStatusCode.OK);
        Reload(app, "ApiPipeline:RateLimiting:Policies:0:PermitLimit", "50");

        (await client.GetAsync("/test", TestContext.Current.CancellationToken)).StatusCode.Should().Be(HttpStatusCode.TooManyRequests,
            "the limit read at startup, 2, is still the one enforced");
    }

    /// <summary>The configured CORS policy keeps applying after a change sets <c>Enabled</c> to false.</summary>
    [Fact]
    public async Task Cors_DisabledAfterStartup_KeepsApplying()
    {
        await using var app = await TestAppBuilder.CreateAppAsync(TestAppBuilder.MinimalConfig(c =>
        {
            c["ApiPipeline:Cors:Enabled"] = "true";
            c["ApiPipeline:Cors:AllowedOrigins:0"] = "https://app.example";
        }));
        app.UseApiPipeline(pipeline => pipeline.WithCors());
        app.MapGet("/test", () => "ok");
        await app.StartAsync(TestContext.Current.CancellationToken);
        var client = app.GetTestClient();

        Reload(app, "ApiPipeline:Cors:Enabled", "false");
        using var request = new HttpRequestMessage(HttpMethod.Get, "/test");
        request.Headers.Add("Origin", "https://app.example");
        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.Headers.GetValues("Access-Control-Allow-Origin").Should().ContainSingle("https://app.example");
    }

    /// <summary>An origin added after startup is not allowed until restart.</summary>
    [Fact]
    public async Task Cors_OriginAddedAfterStartup_IsNotAllowed()
    {
        await using var app = await TestAppBuilder.CreateAppAsync(TestAppBuilder.MinimalConfig(c =>
        {
            c["ApiPipeline:Cors:Enabled"] = "true";
            c["ApiPipeline:Cors:AllowedOrigins:0"] = "https://app.example";
        }));
        app.UseApiPipeline(pipeline => pipeline.WithCors());
        app.MapGet("/test", () => "ok");
        await app.StartAsync(TestContext.Current.CancellationToken);
        var client = app.GetTestClient();

        Reload(app, "ApiPipeline:Cors:AllowedOrigins:1", "https://new.example");
        using var request = new HttpRequestMessage(HttpMethod.Get, "/test");
        request.Headers.Add("Origin", "https://new.example");
        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.Headers.Contains("Access-Control-Allow-Origin").Should().BeFalse();
    }

    /// <summary>A path added to the compression exclusions after startup is still compressed.</summary>
    [Fact]
    public async Task ResponseCompression_ExclusionAddedAfterStartup_IsNotApplied()
    {
        await using var app = await TestAppBuilder.CreateAppAsync(TestAppBuilder.MinimalConfig(c =>
        {
            c["ApiPipeline:ResponseCompression:Enabled"] = "true";
            c["ApiPipeline:ResponseCompression:EnableForHttps"] = "true";
            c["ApiPipeline:ResponseCompression:ExcludedPaths:0"] = "/never-compressed";
        }));
        app.UseApiPipeline(pipeline => pipeline.WithResponseCompression());
        app.MapGet("/data", () => Results.Text(new string('a', 4096), "application/json"));
        await app.StartAsync(TestContext.Current.CancellationToken);
        var client = app.GetTestClient();

        Reload(app, "ApiPipeline:ResponseCompression:ExcludedPaths:1", "/data");
        using var request = new HttpRequestMessage(HttpMethod.Get, "/data");
        request.Headers.TryAddWithoutValidation("Accept-Encoding", "br");
        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.Content.Headers.ContentEncoding.Should().Contain("br");
    }
}
