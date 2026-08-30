using System.Net;
using ApiPipeline.NET.Pipeline;
using FluentAssertions;
using Microsoft.AspNetCore.TestHost;

namespace ApiPipeline.NET.Tests;

/// <summary>
/// Forwarded headers processing; trust-boundary cases are in
/// <see cref="ForwardedHeadersTrustBoundaryTests"/>. Tests set a trusted loopback peer.
/// </summary>
public sealed class ForwardedHeadersTests
{
    /// <summary>
    /// Verifies that the middleware reads the <c>X-Forwarded-For</c> header and applies
    /// it so that <see cref="Microsoft.AspNetCore.Http.ConnectionInfo.RemoteIpAddress"/>
    /// reflects the client IP reported by the proxy.
    /// </summary>
    [Fact]
    public async Task UseApiPipelineForwardedHeaders_Applies_XForwardedFor()
    {
        var config = TestAppBuilder.MinimalConfig();
        await using var app = await TestAppBuilder.CreateAppAsync(config);

        app.Use(async (ctx, next) =>
        {
            // Loopback is the default trusted proxy; TestServer supplies no peer address, and the
            // package will not honour forwarded headers from an unidentifiable peer.
            ctx.Connection.RemoteIpAddress = System.Net.IPAddress.Loopback;
            await next(ctx);
        });
        app.UseApiPipeline(pipeline => pipeline.WithForwardedHeaders());
        app.MapGet("/ip", (HttpContext ctx) =>
            Results.Ok(new { ip = ctx.Connection.RemoteIpAddress?.ToString() }));
        await app.StartAsync(TestContext.Current.CancellationToken);

        var client = app.GetTestClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "/ip");
        request.Headers.Add("X-Forwarded-For", "203.0.113.42");

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.Should().Contain("203.0.113.42");
    }

    /// <summary>
    /// Verifies that the middleware handles requests without any forwarded headers
    /// gracefully and does not throw an exception.
    /// </summary>
    [Fact]
    public async Task UseApiPipelineForwardedHeaders_DoesNotThrow_Without_Headers()
    {
        var config = TestAppBuilder.MinimalConfig();
        await using var app = await TestAppBuilder.CreateAppAsync(config);

        app.Use(async (ctx, next) =>
        {
            // Loopback is the default trusted proxy; TestServer supplies no peer address, and the
            // package will not honour forwarded headers from an unidentifiable peer.
            ctx.Connection.RemoteIpAddress = System.Net.IPAddress.Loopback;
            await next(ctx);
        });
        app.UseApiPipeline(pipeline => pipeline.WithForwardedHeaders());
        app.MapGet("/test", () => Results.Ok("ok"));
        await app.StartAsync(TestContext.Current.CancellationToken);

        var client = app.GetTestClient();
        var response = await client.GetAsync("/test", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>
    /// Verifies that when the forwarded-headers feature is disabled, the
    /// <c>X-Forwarded-For</c> header is ignored and <c>RemoteIpAddress</c>
    /// is not overwritten with the forwarded value.
    /// </summary>
    [Fact]
    public async Task UseApiPipelineForwardedHeaders_Disabled_Does_Not_Process_Headers()
    {
        var config = TestAppBuilder.MinimalConfig(c =>
        {
            c["ApiPipeline:ForwardedHeaders:Enabled"] = "false";
        });
        await using var app = await TestAppBuilder.CreateAppAsync(config);

        app.Use(async (ctx, next) =>
        {
            // Loopback is the default trusted proxy; TestServer supplies no peer address, and the
            // package will not honour forwarded headers from an unidentifiable peer.
            ctx.Connection.RemoteIpAddress = System.Net.IPAddress.Loopback;
            await next(ctx);
        });
        app.UseApiPipeline(pipeline => pipeline.WithForwardedHeaders());
        app.MapGet("/ip", (HttpContext ctx) =>
            Results.Ok(new { ip = ctx.Connection.RemoteIpAddress?.ToString() }));
        await app.StartAsync(TestContext.Current.CancellationToken);

        var client = app.GetTestClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "/ip");
        request.Headers.Add("X-Forwarded-For", "203.0.113.42");

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.Should().NotContain("203.0.113.42");
    }

    /// <summary>
    /// Verifies that <c>ForwardLimit</c> is respected: when set to 1, only the
    /// rightmost IP in a multi-hop <c>X-Forwarded-For</c> chain is applied, and
    /// earlier hops are ignored.
    /// </summary>
    [Fact]
    public async Task UseApiPipelineForwardedHeaders_Respects_ForwardLimit()
    {
        var config = TestAppBuilder.MinimalConfig(c =>
        {
            c["ApiPipeline:ForwardedHeaders:Enabled"] = "true";
            c["ApiPipeline:ForwardedHeaders:ForwardLimit"] = "1";
            c["ApiPipeline:ForwardedHeaders:ClearDefaultProxies"] = "true";
        });
        await using var app = await TestAppBuilder.CreateAppAsync(config);

        app.Use(async (ctx, next) =>
        {
            // Loopback is the default trusted proxy; TestServer supplies no peer address, and the
            // package will not honour forwarded headers from an unidentifiable peer.
            ctx.Connection.RemoteIpAddress = System.Net.IPAddress.Loopback;
            await next(ctx);
        });
        app.UseApiPipeline(pipeline => pipeline.WithForwardedHeaders());
        app.MapGet("/ip", (HttpContext ctx) =>
            Results.Ok(new { ip = ctx.Connection.RemoteIpAddress?.ToString() }));
        await app.StartAsync(TestContext.Current.CancellationToken);

        var client = app.GetTestClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "/ip");
        request.Headers.Add("X-Forwarded-For", "198.51.100.1, 203.0.113.42");

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.Should().Contain("203.0.113.42");
        body.Should().NotContain("198.51.100.1");
    }

    /// <summary>
    /// Verifies that when <c>ClearDefaultProxies</c> is enabled and <c>ForwardLimit</c>
    /// allows multiple hops, all listed IPs in the forwarded chain are trusted and the
    /// first hop IP is applied as the remote address.
    /// </summary>
    [Fact]
    public async Task UseApiPipelineForwardedHeaders_ClearDefaultProxies_Trusts_All_Listed()
    {
        var config = TestAppBuilder.MinimalConfig(c =>
        {
            c["ApiPipeline:ForwardedHeaders:Enabled"] = "true";
            c["ApiPipeline:ForwardedHeaders:ForwardLimit"] = "2";
            c["ApiPipeline:ForwardedHeaders:ClearDefaultProxies"] = "true";
        });
        await using var app = await TestAppBuilder.CreateAppAsync(config);

        app.Use(async (ctx, next) =>
        {
            // Loopback is the default trusted proxy; TestServer supplies no peer address, and the
            // package will not honour forwarded headers from an unidentifiable peer.
            ctx.Connection.RemoteIpAddress = System.Net.IPAddress.Loopback;
            await next(ctx);
        });
        app.UseApiPipeline(pipeline => pipeline.WithForwardedHeaders());
        app.MapGet("/ip", (HttpContext ctx) =>
            Results.Ok(new { ip = ctx.Connection.RemoteIpAddress?.ToString() }));
        await app.StartAsync(TestContext.Current.CancellationToken);

        var client = app.GetTestClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "/ip");
        request.Headers.Add("X-Forwarded-For", "198.51.100.1, 203.0.113.42");

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.Should().Contain("198.51.100.1");
    }

    /// <summary>
    /// A trust entry that does not parse fails validation in every environment, Development
    /// included: skipped, it would leave a narrower trust set than written, and every client behind
    /// the proxy it meant would share the proxy's address.
    /// </summary>
    [Theory]
    [InlineData("KnownNetworks", "10.0.0.0/999")]
    [InlineData("KnownProxies", "not-an-ip-address")]
    public async Task InvalidTrustEntry_NextToAValidOne_FailsValidation(string list, string entry)
    {
        var config = TestAppBuilder.MinimalConfig(c =>
        {
            c["ApiPipeline:ForwardedHeaders:Enabled"] = "true";
            c["ApiPipeline:ForwardedHeaders:ClearDefaultProxies"] = "true";
            c[$"ApiPipeline:ForwardedHeaders:{list}:0"] = entry;
            c[$"ApiPipeline:ForwardedHeaders:{list}:1"] = list == "KnownProxies" ? "10.0.0.1" : "10.0.0.0/8";
        });

        await using var app = await TestAppBuilder.CreateAppAsync(config, Environments.Development);

        var act = () => app.UseApiPipeline(pipeline => pipeline.WithForwardedHeaders());

        act.Should().Throw<Microsoft.Extensions.Options.OptionsValidationException>()
            .WithMessage($"*{list} entry '{entry}'*");
    }

    /// <summary>
    /// The framework's parser reads shorthand, octal and hex IPv4 as other addresses (10.0.10 is
    /// 10.0.0.10, 010.0.0.1 is 8.0.0.1, 10/8 is 0.0.0.0/8), which would trust the wrong proxy:
    /// IPv4 must be four decimal numbers.
    /// </summary>
    [Theory]
    [InlineData("KnownProxies", "10.0.10")]
    [InlineData("KnownProxies", "010.0.0.1")]
    [InlineData("KnownProxies", "0x0A.0.0.1")]
    [InlineData("KnownNetworks", "10/8")]
    [InlineData("KnownNetworks", "010.0.0.0/8")]
    public async Task AmbiguousIPv4TrustEntry_FailsValidation(string list, string entry)
    {
        var config = TestAppBuilder.MinimalConfig(c =>
        {
            c["ApiPipeline:ForwardedHeaders:Enabled"] = "true";
            c[$"ApiPipeline:ForwardedHeaders:{list}:0"] = entry;
        });

        await using var app = await TestAppBuilder.CreateAppAsync(config, Environments.Development);

        var act = () => app.UseApiPipeline(pipeline => pipeline.WithForwardedHeaders());

        act.Should().Throw<Microsoft.Extensions.Options.OptionsValidationException>()
            .WithMessage("*four decimal numbers*");
    }

    /// <summary>Disabled, the lists are not read, so they are not checked either.</summary>
    [Fact]
    public async Task InvalidTrustEntry_WithForwardedHeadersDisabled_DoesNotFail()
    {
        var config = TestAppBuilder.MinimalConfig(c =>
        {
            c["ApiPipeline:ForwardedHeaders:Enabled"] = "false";
            c["ApiPipeline:ForwardedHeaders:KnownProxies:0"] = "not-an-ip-address";
        });

        await using var app = await TestAppBuilder.CreateAppAsync(config, Environments.Production);

        var act = () => app.UseApiPipeline(pipeline => pipeline.WithForwardedHeaders());

        act.Should().NotThrow();
    }

    /// <summary>
    /// <c>ForwardLimit: 1</c> reads only the closest hop: the outer <c>1.2.3.4</c> is not the client.
    /// </summary>
    [Fact]
    public async Task UseApiPipelineForwardedHeaders_ForwardLimit_Constrains_XFF_Hops()
    {
        // ForwardLimit=1 means only the closest (right-most) XFF entry is applied
        var config = TestAppBuilder.MinimalConfig(c =>
        {
            c["ApiPipeline:ForwardedHeaders:Enabled"] = "true";
            c["ApiPipeline:ForwardedHeaders:ForwardLimit"] = "1";
        });
        await using var app = await TestAppBuilder.CreateAppAsync(config);

        app.Use(async (ctx, next) =>
        {
            // Loopback is the default trusted proxy; TestServer supplies no peer address, and the
            // package will not honour forwarded headers from an unidentifiable peer.
            ctx.Connection.RemoteIpAddress = System.Net.IPAddress.Loopback;
            await next(ctx);
        });
        app.UseApiPipeline(pipeline => pipeline.WithForwardedHeaders());
        app.MapGet("/ip", (HttpContext ctx) =>
            Results.Ok(new { ip = ctx.Connection.RemoteIpAddress?.ToString() }));
        await app.StartAsync(TestContext.Current.CancellationToken);

        var client = app.GetTestClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "/ip");
        // Two hops: outer (spoofed) = 1.2.3.4, closest (trusted proxy) = 203.0.113.42
        request.Headers.Add("X-Forwarded-For", "1.2.3.4, 203.0.113.42");

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        // Only the closest hop (203.0.113.42) is applied with ForwardLimit=1
        body.Should().Contain("203.0.113.42");
        body.Should().NotContain("1.2.3.4");
    }
}
