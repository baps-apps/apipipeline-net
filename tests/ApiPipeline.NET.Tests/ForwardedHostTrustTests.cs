using System.Net;
using ApiPipeline.NET.Pipeline;
using FluentAssertions;
using Microsoft.AspNetCore.TestHost;

namespace ApiPipeline.NET.Tests;

/// <summary>
/// <c>X-Forwarded-Host</c> is ignored unless <c>ProcessHost</c> is on with a real host allow-list.
/// TestServer has no peer address, so each test sets one.
/// </summary>
public sealed class ForwardedHostTrustTests
{
    private const string TrustedPeer = "10.0.0.5";
    private const string SpoofedHost = "attacker.example.com";

    private static Dictionary<string, string?> Config(bool? processHost = null, string? allowedHosts = null) =>
        TestAppBuilder.MinimalConfig(c =>
        {
            c["ApiPipeline:ForwardedHeaders:Enabled"] = "true";
            c["ApiPipeline:ForwardedHeaders:ClearDefaultProxies"] = "true";
            c["ApiPipeline:ForwardedHeaders:KnownNetworks:0"] = "10.0.0.0/8";
            if (processHost is { } value)
            {
                c["ApiPipeline:ForwardedHeaders:ProcessHost"] = value ? "true" : "false";
            }

            if (allowedHosts is not null)
            {
                c["AllowedHosts"] = allowedHosts;
            }
        });

    private static async Task<HttpContext> SendFromTrustedProxyAsync(WebApplication app)
    {
        app.Use(async (ctx, next) =>
        {
            ctx.Connection.RemoteIpAddress = IPAddress.Parse(TrustedPeer);
            await next(ctx);
        });
        app.UseApiPipeline(pipeline => pipeline.WithForwardedHeaders());
        app.MapGet("/probe", () => Results.Ok("ok"));
        await app.StartAsync();

        return await app.GetTestServer().SendAsync(ctx =>
        {
            ctx.Request.Path = "/probe";
            ctx.Request.Headers["X-Forwarded-Host"] = SpoofedHost;
            ctx.Request.Headers["X-Forwarded-Proto"] = "https";
            ctx.Request.Headers["X-Forwarded-For"] = "198.51.100.77";
        });
    }

    /// <summary>
    /// By default a trusted proxy's <c>X-Forwarded-Host</c> does not reach <c>Request.Host</c>.
    /// </summary>
    [Fact]
    public async Task ForwardedHost_FromTrustedProxy_IsIgnoredByDefault()
    {
        await using var app = await TestAppBuilder.CreateAppAsync(Config(), Environments.Production);

        var context = await SendFromTrustedProxyAsync(app);

        context.Request.Host.Host.Should().NotBe(SpoofedHost);
    }

    /// <summary>
    /// Ignoring <c>X-Forwarded-Host</c> must not disturb the two headers the pipeline genuinely
    /// depends on: the client address that partitions rate limiting, and the scheme that gates HSTS.
    /// </summary>
    [Fact]
    public async Task ForwardedHost_Default_StillAppliesForAndProto()
    {
        await using var app = await TestAppBuilder.CreateAppAsync(Config(), Environments.Production);

        var context = await SendFromTrustedProxyAsync(app);

        context.Connection.RemoteIpAddress!.ToString().Should().Be("198.51.100.77");
        context.Request.Scheme.Should().Be("https");
    }

    /// <summary>
    /// A deployment whose proxy sets <c>X-Forwarded-Host</c> itself, and strips any inbound value,
    /// can still opt in — paired with a real <c>AllowedHosts</c>, which is what makes the resulting
    /// host bounded rather than caller-chosen.
    /// </summary>
    [Fact]
    public async Task ForwardedHost_IsApplied_WhenProcessHostIsExplicitlyEnabled()
    {
        await using var app = await TestAppBuilder.CreateAppAsync(
            // "localhost" is the Host TestServer connects with, and host filtering — which
            // WebApplication inserts ahead of every middleware — rejects a connection whose Host is
            // not on this list before forwarded headers ever run. That is the pairing working as
            // intended: an allow-list constrains both the connection host and the forwarded one.
            Config(processHost: true, allowedHosts: $"localhost;{SpoofedHost}"),
            Environments.Production);

        var context = await SendFromTrustedProxyAsync(app);

        context.Request.Host.Host.Should().Be(SpoofedHost);
    }

    /// <summary>
    /// Host filtering runs ahead of every application middleware, so it only ever sees the
    /// connection's <c>Host</c>. A forwarded host outside <c>AllowedHosts</c> must still be
    /// refused when forwarded headers are applied, or a proxy that passes the caller's
    /// <c>X-Forwarded-Host</c> through lets the caller choose <c>Request.Host</c>.
    /// </summary>
    [Fact]
    public async Task ForwardedHost_NotInAllowedHosts_IsNotApplied()
    {
        await using var app = await TestAppBuilder.CreateAppAsync(
            // Only the connection host: host filtering passes, the forwarded host is not listed.
            Config(processHost: true, allowedHosts: "localhost"),
            Environments.Production);

        var context = await SendFromTrustedProxyAsync(app);

        context.Request.Host.Host.Should().Be("localhost");
    }

    /// <summary>
    /// The allow-list keeps host filtering's syntax when it bounds the forwarded host: a
    /// subdomain wildcard admits a matching forwarded host.
    /// </summary>
    [Fact]
    public async Task ForwardedHost_MatchingSubdomainWildcard_IsApplied()
    {
        await using var app = await TestAppBuilder.CreateAppAsync(
            Config(processHost: true, allowedHosts: "localhost;*.example.com"),
            Environments.Production);

        var context = await SendFromTrustedProxyAsync(app);

        context.Request.Host.Host.Should().Be(SpoofedHost);
    }

    /// <summary>
    /// <c>ProcessHost = true</c> without a real <c>AllowedHosts</c> list fails Production startup.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("*")]
    [InlineData("")]
    [InlineData("api.example.com;*")]
    public async Task ProcessHost_WithoutHostAllowList_FailsProductionStartup(string? allowedHosts)
    {
        await using var app = await TestAppBuilder.CreateAppAsync(
            Config(processHost: true, allowedHosts: allowedHosts), Environments.Production);

        var act = () => app.UseApiPipeline(pipeline => pipeline.WithForwardedHeaders());

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*ProcessHost is true but AllowedHosts*");
    }

    /// <summary>
    /// The guard keys on Development, not Production, so enterprise environment names fail closed too.
    /// </summary>
    [Theory]
    [InlineData("Prod")]
    [InlineData("prd")]
    [InlineData("Staging")]
    public async Task ProcessHost_WithoutHostAllowList_InAnyNonDevelopmentEnvironment_FailsStartup(string environment)
    {
        await using var app = await TestAppBuilder.CreateAppAsync(Config(processHost: true), environment);

        var act = () => app.UseApiPipeline(pipeline => pipeline.WithForwardedHeaders());

        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"*ProcessHost is true but AllowedHosts*'{environment}'*");
    }

    /// <summary>
    /// The same configuration in Development warns instead of throwing: a developer sees their
    /// own startup log, and failing every local run over a value that only matters behind a real
    /// ingress would be noise.
    /// </summary>
    [Fact]
    public async Task ProcessHost_WithoutHostAllowList_InDevelopment_DoesNotThrow()
    {
        await using var app = await TestAppBuilder.CreateAppAsync(
            Config(processHost: true), Environments.Development);

        var act = () => app.UseApiPipeline(pipeline => pipeline.WithForwardedHeaders());

        act.Should().NotThrow();
    }

    /// <summary>
    /// A multi-host allow-list in configuration satisfies the guard. <c>WebApplication</c> splits
    /// the <c>AllowedHosts</c> key on <c>;</c> only, so that is the separator a real list uses.
    /// </summary>
    [Fact]
    public async Task ProcessHost_WithMultiHostAllowList_DoesNotThrow()
    {
        await using var app = await TestAppBuilder.CreateAppAsync(
            Config(processHost: true, allowedHosts: "api.example.com;api.internal"),
            Environments.Production);

        var act = () => app.UseApiPipeline(pipeline => pipeline.WithForwardedHeaders());

        act.Should().NotThrow();
    }

    /// <summary>
    /// An allow-list configured in code, through <c>HostFilteringOptions</c>, constrains the host
    /// exactly as one in configuration does, and must satisfy the guard. Reading only the raw
    /// <c>AllowedHosts</c> key failed Production startup for an application that was safe.
    /// </summary>
    [Fact]
    public async Task ProcessHost_WithAllowListConfiguredInCode_DoesNotThrow()
    {
        await using var app = await TestAppBuilder.CreateAppAsync(
            Config(processHost: true),
            Environments.Production,
            configureServices: services => services.Configure<Microsoft.AspNetCore.HostFiltering.HostFilteringOptions>(
                o => o.AllowedHosts = ["api.example.com"]));

        var act = () => app.UseApiPipeline(pipeline => pipeline.WithForwardedHeaders());

        act.Should().NotThrow();
    }

    /// <summary>
    /// The converse: code that opens host filtering back up to <c>"*"</c> wins over a list in
    /// configuration, because it is what the host-filtering middleware enforces. The guard must
    /// judge the effective value, not the configuration key.
    /// </summary>
    [Fact]
    public async Task ProcessHost_WithWildcardConfiguredInCode_OverConfiguredList_FailsProductionStartup()
    {
        await using var app = await TestAppBuilder.CreateAppAsync(
            Config(processHost: true, allowedHosts: "api.example.com"),
            Environments.Production,
            configureServices: services => services.Configure<Microsoft.AspNetCore.HostFiltering.HostFilteringOptions>(
                o => o.AllowedHosts = ["*"]));

        var act = () => app.UseApiPipeline(pipeline => pipeline.WithForwardedHeaders());

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*ProcessHost is true but AllowedHosts*");
    }

    /// <summary>
    /// The guard is scoped to the behaviour it protects: with <c>ProcessHost</c> off,
    /// <c>Request.Host</c> comes from the connection and <c>AllowedHosts</c> is irrelevant to it.
    /// </summary>
    [Fact]
    public async Task ProcessHostOff_WithOpenAllowedHosts_DoesNotThrow()
    {
        await using var app = await TestAppBuilder.CreateAppAsync(
            Config(processHost: false, allowedHosts: "*"), Environments.Production);

        var act = () => app.UseApiPipeline(pipeline => pipeline.WithForwardedHeaders());

        act.Should().NotThrow();
    }

    /// <summary>
    /// The default is part of the security contract of the package, not an accident of binding.
    /// </summary>
    [Fact]
    public void ForwardedHeadersSettings_ProcessHost_DefaultsToFalse()
    {
        new ApiPipelineForwardedHeadersOptions().ProcessHost.Should().BeFalse();
    }
}
