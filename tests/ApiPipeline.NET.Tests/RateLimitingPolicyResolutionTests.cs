using System.Net;
using ApiPipeline.NET.Pipeline;
using FluentAssertions;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.TestHost;

namespace ApiPipeline.NET.Tests;

/// <summary>
/// Tests for how the default policy and named per-endpoint policies interact, and for the
/// <c>AnonymousFallback</c> behaviours.
/// A named policy must be able to raise the limit, even though the global limiter applies on top.
/// </summary>
public sealed class RateLimitingPolicyResolutionTests
{
    private static Dictionary<string, string?> TwoPolicyConfig(
        int defaultPermit,
        int namedPermit,
        string anonymousFallback = "RateLimit")
    {
        return TestAppBuilder.MinimalConfig(c =>
        {
            c["ApiPipeline:RateLimiting:Enabled"] = "true";
            c["ApiPipeline:RateLimiting:AnonymousFallback"] = anonymousFallback;
            c["ApiPipeline:RateLimiting:DefaultPolicy"] = "standard";
            c["ApiPipeline:RateLimiting:Policies:0:Name"] = "standard";
            c["ApiPipeline:RateLimiting:Policies:0:Kind"] = "FixedWindow";
            c["ApiPipeline:RateLimiting:Policies:0:PermitLimit"] = defaultPermit.ToString();
            c["ApiPipeline:RateLimiting:Policies:0:Window"] = "00:01:00";
            c["ApiPipeline:RateLimiting:Policies:0:QueueLimit"] = "0";
            c["ApiPipeline:RateLimiting:Policies:1:Name"] = "generous";
            c["ApiPipeline:RateLimiting:Policies:1:Kind"] = "FixedWindow";
            c["ApiPipeline:RateLimiting:Policies:1:PermitLimit"] = namedPermit.ToString();
            c["ApiPipeline:RateLimiting:Policies:1:Window"] = "00:01:00";
            c["ApiPipeline:RateLimiting:Policies:1:QueueLimit"] = "0";
        });
    }

    /// <summary>
    /// An endpoint that opts into a named policy with a higher limit must actually get that higher
    /// limit. If the default policy is applied on top, the endpoint is capped at the default and the
    /// per-endpoint policy is silently inert.
    /// </summary>
    [Fact]
    public async Task RateLimiting_NamedEndpointPolicy_RaisesLimitAboveDefaultPolicy()
    {
        await using var app = await TestAppBuilder.CreateAppAsync(TwoPolicyConfig(defaultPermit: 2, namedPermit: 50));
        app.UseApiPipeline(pipeline => pipeline.WithRateLimiting());
        app.MapGet("/generous", () => Results.Ok("ok")).RequireRateLimiting("generous");
        await app.StartAsync(TestContext.Current.CancellationToken);

        var client = app.GetTestClient();
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 6; i++)
        {
            statuses.Add((await client.GetAsync("/generous", TestContext.Current.CancellationToken)).StatusCode);
        }

        statuses.Should().AllBeEquivalentTo(HttpStatusCode.OK);
    }

    /// <summary>
    /// A named policy with a lower limit must still throttle at its own limit.
    /// </summary>
    [Fact]
    public async Task RateLimiting_NamedEndpointPolicy_LowersLimitBelowDefaultPolicy()
    {
        await using var app = await TestAppBuilder.CreateAppAsync(TwoPolicyConfig(defaultPermit: 50, namedPermit: 2));
        await using var _ = app;
        app.UseApiPipeline(pipeline => pipeline.WithRateLimiting());
        app.MapGet("/strict", () => Results.Ok("ok")).RequireRateLimiting("generous");
        await app.StartAsync(TestContext.Current.CancellationToken);

        var client = app.GetTestClient();
        (await client.GetAsync("/strict", TestContext.Current.CancellationToken)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/strict", TestContext.Current.CancellationToken)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/strict", TestContext.Current.CancellationToken)).StatusCode.Should().Be((HttpStatusCode)429);
    }

    /// <summary>
    /// Endpoints that do not opt into a named policy still get the configured default policy.
    /// </summary>
    [Fact]
    public async Task RateLimiting_EndpointWithoutNamedPolicy_StillGetsDefaultPolicy()
    {
        await using var app = await TestAppBuilder.CreateAppAsync(TwoPolicyConfig(defaultPermit: 2, namedPermit: 50));
        app.UseApiPipeline(pipeline => pipeline.WithRateLimiting());
        app.MapGet("/plain", () => Results.Ok("ok"));
        await app.StartAsync(TestContext.Current.CancellationToken);

        var client = app.GetTestClient();
        (await client.GetAsync("/plain", TestContext.Current.CancellationToken)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/plain", TestContext.Current.CancellationToken)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/plain", TestContext.Current.CancellationToken)).StatusCode.Should().Be((HttpStatusCode)429);
    }

    /// <summary>
    /// <c>RateLimit-Policy</c> quota must report the limit that actually applied, not the default policy's
    /// limit, otherwise a client tuning its backoff is being told the wrong number.
    /// </summary>
    [Fact]
    public async Task RateLimiting_XRateLimitLimit_ReportsEffectiveNamedPolicyLimit()
    {
        var config = TwoPolicyConfig(defaultPermit: 2, namedPermit: 50);
        // The success-path informational headers are opt-in; a 429 carries them either way.
        config["ApiPipeline:RateLimiting:EmitRateLimitHeaders"] = "true";
        await using var app = await TestAppBuilder.CreateAppAsync(config);
        app.UseApiPipeline(pipeline => pipeline.WithRateLimiting());
        app.MapGet("/generous", () => Results.Ok("ok")).RequireRateLimiting("generous");
        await app.StartAsync(TestContext.Current.CancellationToken);

        var response = await app.GetTestClient().GetAsync("/generous", TestContext.Current.CancellationToken);

        RateLimitHeaders.Quota(response).Should().Be(50);
    }

    /// <summary>
    /// The default <c>AnonymousFallback</c> is <c>Reject</c>. When the client IP cannot be determined
    /// it must produce HTTP 429, not an unhandled <see cref="ArgumentException"/> from constructing a
    /// limiter with <c>PermitLimit = 0</c>.
    /// </summary>
    [Fact]
    public async Task RateLimiting_AnonymousFallbackReject_NullRemoteIp_Returns429_NotException()
    {
        var config = TwoPolicyConfig(defaultPermit: 10, namedPermit: 10, anonymousFallback: "Reject");
        await using var app = await TestAppBuilder.CreateAppAsync(config);
        app.UseApiPipeline(pipeline => pipeline.WithRateLimiting());
        app.MapGet("/plain", () => Results.Ok("ok"));
        await app.StartAsync(TestContext.Current.CancellationToken);

        // TestServer leaves Connection.RemoteIpAddress null, which is the condition under test.
        var response = await app.GetTestClient().GetAsync("/plain", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be((HttpStatusCode)429);
    }

    /// <summary>
    /// <c>Reject</c> must deny every request in that partition, not admit the first one.
    /// </summary>
    [Fact]
    public async Task RateLimiting_AnonymousFallbackReject_NullRemoteIp_RejectsEveryRequest()
    {
        var config = TwoPolicyConfig(defaultPermit: 10, namedPermit: 10, anonymousFallback: "Reject");
        await using var app = await TestAppBuilder.CreateAppAsync(config);
        app.UseApiPipeline(pipeline => pipeline.WithRateLimiting());
        app.MapGet("/plain", () => Results.Ok("ok"));
        await app.StartAsync(TestContext.Current.CancellationToken);

        var client = app.GetTestClient();
        for (var i = 0; i < 3; i++)
        {
            (await client.GetAsync("/plain", TestContext.Current.CancellationToken)).StatusCode.Should().Be((HttpStatusCode)429);
        }
    }

    /// <summary>
    /// <c>Allow</c> must skip rate limiting entirely when the client IP is unknown.
    /// </summary>
    [Fact]
    public async Task RateLimiting_AnonymousFallbackAllow_NullRemoteIp_AllowsRequest()
    {
        var config = TwoPolicyConfig(defaultPermit: 1, namedPermit: 1, anonymousFallback: "Allow");
        await using var app = await TestAppBuilder.CreateAppAsync(config);
        app.UseApiPipeline(pipeline => pipeline.WithRateLimiting());
        app.MapGet("/plain", () => Results.Ok("ok"));
        await app.StartAsync(TestContext.Current.CancellationToken);

        var client = app.GetTestClient();
        (await client.GetAsync("/plain", TestContext.Current.CancellationToken)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/plain", TestContext.Current.CancellationToken)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>
    /// <c>Retry-After</c> must be present on a fixed-window rejection so clients can back off.
    /// </summary>
    [Fact]
    public async Task RateLimiting_RejectedResponse_IncludesRetryAfter()
    {
        await using var app = await TestAppBuilder.CreateAppAsync(TwoPolicyConfig(defaultPermit: 1, namedPermit: 1));
        app.UseApiPipeline(pipeline => pipeline.WithRateLimiting());
        app.MapGet("/plain", () => Results.Ok("ok"));
        await app.StartAsync(TestContext.Current.CancellationToken);

        var client = app.GetTestClient();
        await client.GetAsync("/plain", TestContext.Current.CancellationToken);
        var rejected = await client.GetAsync("/plain", TestContext.Current.CancellationToken);

        rejected.StatusCode.Should().Be((HttpStatusCode)429);
        rejected.Headers.RetryAfter.Should().NotBeNull();
    }

    /// <summary>
    /// The window length is the <c>w</c> parameter of <c>RateLimit-Policy</c>. A successful response
    /// carries no <c>RateLimit</c> field, whose <c>r</c>/<c>t</c> the package does not track for a
    /// framework-owned partition and therefore must not claim.
    /// </summary>
    [Fact]
    public async Task RateLimiting_EmitsWindowHeader_AndNotMisleadingResetHeader()
    {
        var config = TwoPolicyConfig(defaultPermit: 10, namedPermit: 10);
        config["ApiPipeline:RateLimiting:EmitRateLimitHeaders"] = "true";
        await using var app = await TestAppBuilder.CreateAppAsync(config);
        app.UseApiPipeline(pipeline => pipeline.WithRateLimiting());
        app.MapGet("/plain", () => Results.Ok("ok"));
        await app.StartAsync(TestContext.Current.CancellationToken);

        var response = await app.GetTestClient().GetAsync("/plain", TestContext.Current.CancellationToken);

        RateLimitHeaders.Window(response).Should().Be(60);
        RateLimitHeaders.Status(response).Should().BeNull();
    }

    /// <summary>
    /// <c>[DisableRateLimiting]</c> / <c>DisableRateLimiting()</c> must bypass the default policy too.
    /// </summary>
    [Fact]
    public async Task RateLimiting_EndpointWithDisableRateLimiting_IsNotLimited()
    {
        await using var app = await TestAppBuilder.CreateAppAsync(TwoPolicyConfig(defaultPermit: 1, namedPermit: 1));
        app.UseApiPipeline(pipeline => pipeline.WithRateLimiting());
        app.MapGet("/free", () => Results.Ok("ok")).DisableRateLimiting();
        await app.StartAsync(TestContext.Current.CancellationToken);

        var client = app.GetTestClient();
        (await client.GetAsync("/free", TestContext.Current.CancellationToken)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/free", TestContext.Current.CancellationToken)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/free", TestContext.Current.CancellationToken)).StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
