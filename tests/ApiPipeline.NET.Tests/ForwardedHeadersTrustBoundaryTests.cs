using System.Net;
using ApiPipeline.NET.Pipeline;
using FluentAssertions;
using Microsoft.AspNetCore.TestHost;

namespace ApiPipeline.NET.Tests;

/// <summary>
/// Trust-boundary tests: an empty trust set trusts every peer, so it must never happen by accident.
/// TestServer has no peer address, so each test sets one.
/// </summary>
public sealed class ForwardedHeadersTrustBoundaryTests
{
    private const string TrustedPeer = "10.0.0.5";
    private const string UntrustedPeer = "203.0.113.1";
    private const string SpoofedClientIp = "198.51.100.77";

    private static Dictionary<string, string?> TrustConfig(
        string? knownNetwork = "10.0.0.0/8",
        bool clearDefaults = true,
        bool enforce = true)
    {
        return TestAppBuilder.MinimalConfig(config =>
        {
            config["ApiPipeline:ForwardedHeaders:Enabled"] = "true";
            config["ApiPipeline:ForwardedHeaders:ClearDefaultProxies"] = clearDefaults ? "true" : "false";
            config["ApiPipeline:ForwardedHeaders:EnforceTrustedProxyConfiguration"] =
                enforce ? "true" : "false";
            if (knownNetwork is not null)
            {
                config["ApiPipeline:ForwardedHeaders:KnownNetworks:0"] = knownNetwork;
            }
        });
    }

    private static async Task<HttpResponseMessage> SendThroughPeerAsync(
        WebApplication app,
        string peerIp,
        Action<HttpRequestMessage> configureRequest)
    {
        app.Use(async (ctx, next) =>
        {
            ctx.Connection.RemoteIpAddress = IPAddress.Parse(peerIp);
            await next(ctx);
        });
        app.UseApiPipeline(pipeline => pipeline.WithForwardedHeaders());
        app.MapGet("/probe", (HttpContext ctx) => Results.Ok(new
        {
            ip = ctx.Connection.RemoteIpAddress?.ToString(),
            scheme = ctx.Request.Scheme,
            host = ctx.Request.Host.Value
        }));
        await app.StartAsync();

        var request = new HttpRequestMessage(HttpMethod.Get, "/probe");
        configureRequest(request);
        return await app.GetTestClient().SendAsync(request);
    }

    /// <summary>
    /// A peer outside the trusted network cannot rewrite the client IP, which rate limiting
    /// partitions on.
    /// </summary>
    [Fact]
    public async Task ForwardedHeaders_UntrustedProxy_CannotSpoofClientIp()
    {
        await using var app = await TestAppBuilder.CreateAppAsync(TrustConfig(), Environments.Production);

        var response = await SendThroughPeerAsync(app, UntrustedPeer,
            r => r.Headers.Add("X-Forwarded-For", SpoofedClientIp));

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.Should().Contain(UntrustedPeer);
        body.Should().NotContain(SpoofedClientIp);
    }

    /// <summary>
    /// A peer inside the configured trusted network is honoured — the feature must still work.
    /// </summary>
    [Fact]
    public async Task ForwardedHeaders_TrustedProxy_AppliesForwardedClientIp()
    {
        await using var app = await TestAppBuilder.CreateAppAsync(TrustConfig(), Environments.Production);

        var response = await SendThroughPeerAsync(app, TrustedPeer,
            r => r.Headers.Add("X-Forwarded-For", SpoofedClientIp));

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.Should().Contain(SpoofedClientIp);
    }

    /// <summary>
    /// An untrusted peer must not be able to set <c>Request.Scheme</c> to https, which would
    /// defeat HTTPS redirection and any <c>IsHttps</c> check.
    /// </summary>
    [Fact]
    public async Task ForwardedHeaders_UntrustedProxy_CannotSpoofXForwardedProto()
    {
        await using var app = await TestAppBuilder.CreateAppAsync(TrustConfig(), Environments.Production);

        var response = await SendThroughPeerAsync(app, UntrustedPeer,
            r => r.Headers.Add("X-Forwarded-Proto", "https"));

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.Should().Contain("\"scheme\":\"http\"");
    }

    /// <summary>
    /// An untrusted peer must not be able to poison <c>Request.Host</c>, which flows into
    /// generated absolute URLs (password resets, pagination links) and cache keys.
    /// </summary>
    [Fact]
    public async Task ForwardedHeaders_UntrustedProxy_CannotSpoofXForwardedHost()
    {
        await using var app = await TestAppBuilder.CreateAppAsync(TrustConfig(), Environments.Production);

        var response = await SendThroughPeerAsync(app, UntrustedPeer,
            r => r.Headers.Add("X-Forwarded-Host", "evil.example.com"));

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.Should().NotContain("evil.example.com");
    }

    /// <summary>
    /// With no peer address (e.g. Kestrel on a Unix socket) the framework would skip its trust
    /// check, so forwarded headers must not be applied at all.
    /// </summary>
    [Fact]
    public async Task ForwardedHeaders_UnknownPeerAddress_DoesNotApplyForwardedHeaders()
    {
        await using var app = await TestAppBuilder.CreateAppAsync(TrustConfig(), Environments.Production);

        // No peer address is stamped: TestServer leaves Connection.RemoteIpAddress null, which is
        // exactly the Unix-domain-socket condition under test.
        app.UseApiPipeline(pipeline => pipeline.WithForwardedHeaders());
        app.MapGet("/probe", (HttpContext ctx) => Results.Ok(new
        {
            ip = ctx.Connection.RemoteIpAddress?.ToString(),
            scheme = ctx.Request.Scheme,
            host = ctx.Request.Host.Value
        }));
        await app.StartAsync(TestContext.Current.CancellationToken);

        var request = new HttpRequestMessage(HttpMethod.Get, "/probe");
        request.Headers.Add("X-Forwarded-For", SpoofedClientIp);
        request.Headers.Add("X-Forwarded-Proto", "https");
        request.Headers.Add("X-Forwarded-Host", "evil.example.com");
        var response = await app.GetTestClient().SendAsync(request, TestContext.Current.CancellationToken);

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.Should().NotContain(SpoofedClientIp);
        body.Should().NotContain("evil.example.com");
        body.Should().Contain("\"scheme\":\"http\"");
    }

    /// <summary>
    /// The dangerous state is an *empty effective trust set*, not <c>ClearDefaultProxies == false</c>.
    /// Clearing the loopback default and configuring nothing must fail startup in Production.
    /// </summary>
    [Fact]
    public async Task ForwardedHeaders_ClearDefaultProxies_WithNoTrustedProxies_InProduction_FailsStartup()
    {
        await using var app = await TestAppBuilder.CreateAppAsync(
            TrustConfig(knownNetwork: null), Environments.Production);

        var act = () => app.UseApiPipeline(pipeline => pipeline.WithForwardedHeaders());

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*effective trusted-proxy set is empty*");
    }

    /// <summary>
    /// A trust set that is non-empty in configuration but would be empty after parsing (every
    /// CIDR invalid) is as dangerous as an empty one: it fails validation.
    /// </summary>
    [Fact]
    public async Task ForwardedHeaders_AllKnownNetworksInvalidCidr_InProduction_FailsStartup()
    {
        await using var app = await TestAppBuilder.CreateAppAsync(
            TrustConfig(knownNetwork: "not-a-cidr"), Environments.Production);

        var act = () => app.UseApiPipeline(pipeline => pipeline.WithForwardedHeaders());

        act.Should().Throw<Microsoft.Extensions.Options.OptionsValidationException>()
            .WithMessage("*KnownNetworks entry 'not-a-cidr'*");
    }

    /// <summary>
    /// The escape hatch must still work: an operator who explicitly opts out of enforcement gets a
    /// running app (and, necessarily, the unsafe behaviour they asked for).
    /// </summary>
    [Fact]
    public async Task ForwardedHeaders_EmptyTrustSet_WithEnforcementDisabled_StartsAndTrustsAnyPeer()
    {
        await using var app = await TestAppBuilder.CreateAppAsync(
            TrustConfig(knownNetwork: null, enforce: false), Environments.Production);

        var response = await SendThroughPeerAsync(app, UntrustedPeer,
            r => r.Headers.Add("X-Forwarded-For", SpoofedClientIp));

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.Should().Contain(SpoofedClientIp);
    }

    /// <summary>
    /// The guard keys on Development, not Production: enterprise environment names (<c>Prod</c>,
    /// <c>prd</c>, <c>PROD-EU</c>, <c>Staging</c>) must fail closed too, or an empty trust set,
    /// which lets every caller spoof its client IP, starts with only a warning.
    /// </summary>
    [Theory]
    [InlineData("Prod")]
    [InlineData("prd")]
    [InlineData("PROD-EU")]
    [InlineData("Staging")]
    [InlineData("Test")]
    public async Task ForwardedHeaders_EmptyTrustSet_InAnyNonDevelopmentEnvironment_FailsStartup(string environment)
    {
        await using var app = await TestAppBuilder.CreateAppAsync(TrustConfig(knownNetwork: null), environment);

        var act = () => app.UseApiPipeline(pipeline => pipeline.WithForwardedHeaders());

        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"*effective trusted-proxy set is empty*'{environment}'*");
    }

    /// <summary>
    /// The loopback-only trust set (no proxy configured) fails closed outside Development as well.
    /// </summary>
    [Theory]
    [InlineData("Prod")]
    [InlineData("Staging")]
    public async Task ForwardedHeaders_LoopbackOnlyTrustSet_InAnyNonDevelopmentEnvironment_FailsStartup(string environment)
    {
        await using var app = await TestAppBuilder.CreateAppAsync(
            TrustConfig(knownNetwork: null, clearDefaults: false), environment);

        var act = () => app.UseApiPipeline(pipeline => pipeline.WithForwardedHeaders());

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*only the loopback address is trusted*");
    }

    /// <summary>
    /// A network holding every client address (<c>/0</c>, or the whole IPv4-mapped range that
    /// dual-stack sockets report) lets any caller set its client IP and scheme.
    /// </summary>
    [Theory]
    [InlineData("0.0.0.0/0")]
    [InlineData("::/0")]
    [InlineData("::ffff:0.0.0.0/96")]
    [InlineData("::/64")]
    public async Task ForwardedHeaders_KnownNetworkContainingEveryClient_InProduction_FailsStartup(string knownNetwork)
    {
        await using var app = await TestAppBuilder.CreateAppAsync(TrustConfig(knownNetwork), Environments.Production);

        var act = () => app.UseApiPipeline(pipeline => pipeline.WithForwardedHeaders());

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*contains every client address*");
    }

    /// <summary>
    /// Networks that hold every client address only together (two IPv4 halves, two IPv4-mapped
    /// halves, or one of each) are caught as well, and the message names each of them.
    /// </summary>
    [Theory]
    [InlineData("0.0.0.0/1", "128.0.0.0/1")]
    [InlineData("::ffff:0:0/97", "::ffff:128.0.0.0/97")]
    [InlineData("0.0.0.0/1", "::ffff:128.0.0.0/97")]
    public async Task ForwardedHeaders_KnownNetworksTogetherContainingEveryClient_InProduction_FailsStartup(
        string first, string second)
    {
        var config = TrustConfig(first);
        config["ApiPipeline:ForwardedHeaders:KnownNetworks:1"] = second;
        await using var app = await TestAppBuilder.CreateAppAsync(config, Environments.Production);

        var act = () => app.UseApiPipeline(pipeline => pipeline.WithForwardedHeaders());

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*together contain every client address*")
            .Which.Message.Should().ContainAll($"'{first}'", $"'{second}'");
    }

    /// <summary>Networks that leave any address out, however little, are still proxies' networks.</summary>
    [Theory]
    [InlineData("0.0.0.0/1", "192.0.0.0/2")]
    [InlineData("0.0.0.0/1", "128.0.0.0/2", "192.0.0.0/3")]
    [InlineData("10.0.0.0/8", "fd00::/8")]
    [InlineData("::ffff:0.0.0.0/97", "8000::/1")]
    public async Task ForwardedHeaders_KnownNetworksLeavingAGap_InProduction_DoesNotThrow(params string[] knownNetworks)
    {
        var config = TrustConfig(knownNetworks[0]);
        for (var i = 1; i < knownNetworks.Length; i++)
        {
            config[$"ApiPipeline:ForwardedHeaders:KnownNetworks:{i}"] = knownNetworks[i];
        }

        await using var app = await TestAppBuilder.CreateAppAsync(config, Environments.Production);

        var act = () => app.UseApiPipeline(pipeline => pipeline.WithForwardedHeaders());

        act.Should().NotThrow();
    }

    /// <summary>Development only warns about a network holding every client address.</summary>
    [Fact]
    public async Task ForwardedHeaders_KnownNetworkContainingEveryClient_InDevelopment_DoesNotThrow()
    {
        await using var app = await TestAppBuilder.CreateAppAsync(TrustConfig("0.0.0.0/0"), Environments.Development);

        var act = () => app.UseApiPipeline(pipeline => pipeline.WithForwardedHeaders());

        act.Should().NotThrow();
    }

    /// <summary>Disabling enforcement accepts a network holding every client address, with its consequence.</summary>
    [Fact]
    public async Task ForwardedHeaders_KnownNetworkContainingEveryClient_WithEnforcementDisabled_StartsAndTrustsAnyPeer()
    {
        await using var app = await TestAppBuilder.CreateAppAsync(
            TrustConfig("0.0.0.0/0", enforce: false), Environments.Production);

        var response = await SendThroughPeerAsync(app, UntrustedPeer,
            r => r.Headers.Add("X-Forwarded-For", SpoofedClientIp));

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.Should().Contain(SpoofedClientIp);
    }

    /// <summary>Broad private ranges, and a part of the IPv4-mapped range, are still proxies' networks.</summary>
    [Theory]
    [InlineData("10.0.0.0/8")]
    [InlineData("fd00::/8")]
    [InlineData("2001:db8::/32")]
    [InlineData("::ffff:10.0.0.0/104")]
    public async Task ForwardedHeaders_KnownNetworkNotContainingEveryClient_InProduction_DoesNotThrow(string knownNetwork)
    {
        await using var app = await TestAppBuilder.CreateAppAsync(TrustConfig(knownNetwork), Environments.Production);

        var act = () => app.UseApiPipeline(pipeline => pipeline.WithForwardedHeaders());

        act.Should().NotThrow();
    }

    /// <summary>
    /// Development must not fail startup on an empty trust set: it is the only environment that warns.
    /// </summary>
    [Fact]
    public async Task ForwardedHeaders_ClearDefaultProxies_WithNoTrustedProxies_InDevelopment_DoesNotThrow()
    {
        await using var app = await TestAppBuilder.CreateAppAsync(
            TrustConfig(knownNetwork: null), Environments.Development);

        var act = () => app.UseApiPipeline(pipeline => pipeline.WithForwardedHeaders());

        act.Should().NotThrow();
    }

    /// <summary>
    /// A CIDR with host bits (<c>10.1.2.3/8</c>) is masked to its network and honoured, not skipped.
    /// </summary>
    [Theory]
    [InlineData("10.1.2.3/8")]
    [InlineData("10.0.0.0/8")]
    public async Task ForwardedHeaders_KnownNetworkWithHostBits_IsMaskedAndTrusted(string knownNetwork)
    {
        await using var app = await TestAppBuilder.CreateAppAsync(
            TrustConfig(knownNetwork), Environments.Production);

        var response = await SendThroughPeerAsync(app, TrustedPeer,
            r => r.Headers.Add("X-Forwarded-For", SpoofedClientIp));

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.Should().Contain(SpoofedClientIp,
            "a peer inside the masked network is trusted, so X-Forwarded-For is applied");
    }

    /// <summary>
    /// The same entry counts as a configured trust entry, so the loopback-only guard does not fire.
    /// </summary>
    [Fact]
    public async Task ForwardedHeaders_KnownNetworkWithHostBits_CountsAsConfigured_AndDoesNotFailStartup()
    {
        await using var app = await TestAppBuilder.CreateAppAsync(
            TestAppBuilder.MinimalConfig(config =>
            {
                config["ApiPipeline:ForwardedHeaders:Enabled"] = "true";
                config["ApiPipeline:ForwardedHeaders:ClearDefaultProxies"] = "false";
                config["ApiPipeline:ForwardedHeaders:EnforceTrustedProxyConfiguration"] = "true";
                config["ApiPipeline:ForwardedHeaders:KnownNetworks:0"] = "10.1.2.3/8";
            }),
            Environments.Production);

        var act = () => app.UseApiPipeline(pipeline => pipeline.WithForwardedHeaders());

        act.Should().NotThrow(
            "the entry parses, so it counts as configured and the loopback-only guard must not fire");
    }

    /// <summary>
    /// A prefix length outside the address family's range, or none, fails validation rather than
    /// being accepted as a different network than intended.
    /// </summary>
    [Theory]
    [InlineData("10.0.0.0/33")]
    [InlineData("10.0.0.0/-1")]
    [InlineData("10.0.0.0")]
    public async Task ForwardedHeaders_KnownNetworkWithInvalidPrefixLength_FailsValidation(string knownNetwork)
    {
        await using var app = await TestAppBuilder.CreateAppAsync(
            TrustConfig(knownNetwork), Environments.Production);

        var act = () => app.UseApiPipeline(pipeline => pipeline.WithForwardedHeaders());

        act.Should().Throw<Microsoft.Extensions.Options.OptionsValidationException>()
            .WithMessage($"*KnownNetworks entry '{knownNetwork}'*");
    }
}
