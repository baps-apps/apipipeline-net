using System.Net;
using ApiPipeline.NET.Pipeline;
using ApiPipeline.NET.RateLimiting;
using FluentAssertions;
using Microsoft.AspNetCore.TestHost;

namespace ApiPipeline.NET.Tests;

/// <summary>
/// Tests for pre-authentication rate limiting — the phase that sheds obvious floods before the
/// request pays for token signature validation.
/// </summary>
public sealed class EdgeRateLimitingTests
{
    private static Dictionary<string, string?> EdgeConfig(int permitLimit, bool? emitOnRejection = null) =>
        TestAppBuilder.MinimalConfig(c =>
        {
            c["ApiPipeline:EdgeRateLimiting:Enabled"] = "true";
            c["ApiPipeline:EdgeRateLimiting:PermitLimit"] =
                permitLimit.ToString(System.Globalization.CultureInfo.InvariantCulture);
            c["ApiPipeline:EdgeRateLimiting:Window"] = "00:10:00";
            if (emitOnRejection is { } emit)
            {
                c["ApiPipeline:RateLimiting:EmitRateLimitHeadersOnRejection"] = emit ? "true" : "false";
            }
        });

    private static async Task<HttpResponseMessage> RejectFromEdgeAsync(WebApplication app)
    {
        app.Use(async (ctx, next) =>
        {
            ctx.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("203.0.113.9");
            await next(ctx);
        });
        app.UseApiPipeline(pipeline => pipeline.WithEdgeRateLimiting());
        app.MapGet("/test", () => Results.Ok("ok"));
        await app.StartAsync();
        var client = app.GetTestClient();

        await client.GetAsync("/test");
        return await client.GetAsync("/test");
    }

    /// <summary>
    /// The whole point of the phase: an excess request is rejected without authentication ever
    /// running, so a flood does not cost signature validation per request.
    /// </summary>
    [Fact]
    public async Task EdgeRateLimiting_ShedsRequests_BeforeAuthenticationRuns()
    {
        var config = EdgeConfig(2);
        await using var app = await TestAppBuilder.CreateAppAsync(config);

        var authenticationRuns = 0;
        app.Use(async (ctx, next) =>
        {
            // TestServer leaves the peer address unset; the edge limiter deliberately does not shed
            // a request it cannot attribute to a client, so give it one.
            ctx.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("203.0.113.7");
            await next(ctx);
        });
        app.UseApiPipeline(pipeline => pipeline.WithEdgeRateLimiting());
        app.Use(async (ctx, next) =>
        {
            Interlocked.Increment(ref authenticationRuns);
            await next(ctx);
        });
        app.MapGet("/test", () => Results.Ok("ok"));
        await app.StartAsync(TestContext.Current.CancellationToken);
        var client = app.GetTestClient();

        (await client.GetAsync("/test", TestContext.Current.CancellationToken)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/test", TestContext.Current.CancellationToken)).StatusCode.Should().Be(HttpStatusCode.OK);
        var rejected = await client.GetAsync("/test", TestContext.Current.CancellationToken);

        ((int)rejected.StatusCode).Should().Be(429);
        authenticationRuns.Should().Be(2,
            "the shed request must never reach the middleware that would validate its token");
        rejected.Headers.Contains("Retry-After").Should().BeTrue();
        rejected.Headers.CacheControl?.NoStore.Should().BeTrue();
    }

    /// <summary>
    /// Disabled by default, and inert when disabled. The caller has an address and the limit is one
    /// request, so only <c>Enabled=false</c> keeps the later requests from a 429 (without an
    /// address the limiter skips the request, and this would pass whatever the flag did).
    /// </summary>
    [Fact]
    public async Task EdgeRateLimiting_Disabled_DoesNotLimit()
    {
        var config = EdgeConfig(1).Also(c => c["ApiPipeline:EdgeRateLimiting:Enabled"] = "false");
        await using var app = await TestAppBuilder.CreateAppAsync(config);
        app.Use((ctx, next) =>
        {
            ctx.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("203.0.113.9");
            return next(ctx);
        });
        app.UseApiPipeline(pipeline => pipeline.WithEdgeRateLimiting());
        app.MapGet("/test", () => Results.Ok("ok"));
        await app.StartAsync(TestContext.Current.CancellationToken);
        var client = app.GetTestClient();

        for (var i = 0; i < 20; i++)
        {
            (await client.GetAsync("/test", TestContext.Current.CancellationToken)).StatusCode.Should().Be(HttpStatusCode.OK);
        }
    }

    /// <summary>
    /// A probe that is shed during a flood makes Kubernetes restart a pod that is defending itself
    /// correctly, so probe paths are excluded by default. The caller has an address, and the same
    /// caller is limited on another path, so the probe passes because of the exclusion, not
    /// because the limiter skipped a request without an address.
    /// </summary>
    [Fact]
    public async Task EdgeRateLimiting_ExcludedPaths_AreNotLimited()
    {
        var config = EdgeConfig(1);
        await using var app = await TestAppBuilder.CreateAppAsync(config);
        app.Use((ctx, next) =>
        {
            ctx.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("10.1.2.3");
            return next(ctx);
        });
        app.UseApiPipeline(pipeline => pipeline.WithEdgeRateLimiting());
        app.MapGet("/health", () => Results.Ok("healthy"));
        app.MapGet("/test", () => Results.Ok("ok"));
        await app.StartAsync(TestContext.Current.CancellationToken);
        var client = app.GetTestClient();

        for (var i = 0; i < 10; i++)
        {
            (await client.GetAsync("/health", TestContext.Current.CancellationToken)).StatusCode.Should().Be(HttpStatusCode.OK,
                "the readiness probe path must never be rate limited");
        }

        (await client.GetAsync("/test", TestContext.Current.CancellationToken)).StatusCode.Should().Be(HttpStatusCode.OK);
        ((int)(await client.GetAsync("/test", TestContext.Current.CancellationToken)).StatusCode).Should().Be(429,
            "the same caller is limited off the probe path");
    }

    /// <summary>
    /// A browser preflight must be answered, not shed — the edge phase sits after CORS in the
    /// pipeline for exactly this reason.
    /// </summary>
    [Fact]
    public async Task EdgeRateLimiting_CorsPreflight_IsAnsweredNotShed()
    {
        var config = TestAppBuilder.MinimalConfig(c =>
        {
            c["ApiPipeline:EdgeRateLimiting:Enabled"] = "true";
            c["ApiPipeline:EdgeRateLimiting:PermitLimit"] = "1";
            c["ApiPipeline:EdgeRateLimiting:Window"] = "00:10:00";
            c["ApiPipeline:Cors:Enabled"] = "true";
            c["ApiPipeline:Cors:AllowedOrigins:0"] = "https://app.example";
        });
        await using var app = await TestAppBuilder.CreateAppAsync(config);
        app.UseApiPipeline(pipeline => pipeline.WithCors().WithEdgeRateLimiting());
        app.MapGet("/test", () => Results.Ok("ok"));
        await app.StartAsync(TestContext.Current.CancellationToken);
        var client = app.GetTestClient();

        for (var i = 0; i < 5; i++)
        {
            var preflight = new HttpRequestMessage(HttpMethod.Options, "/test");
            preflight.Headers.Add("Origin", "https://app.example");
            preflight.Headers.Add("Access-Control-Request-Method", "GET");
            var response = await client.SendAsync(preflight, TestContext.Current.CancellationToken);

            ((int)response.StatusCode).Should().NotBe(429,
                "CORS answers the preflight before the edge limiter sees it");
        }
    }

    /// <summary>
    /// Addresses inside one IPv6 /64 are one client to the edge limiter as well, using the same
    /// prefix setting as the main limiter so the two cannot disagree.
    /// </summary>
    [Fact]
    public async Task EdgeRateLimiting_UsesSharedIPv6PrefixSetting()
    {
        var config = EdgeConfig(2);
        config["ApiPipeline:RateLimiting:IPv6PartitionPrefixLength"] = "48";
        await using var app = await TestAppBuilder.CreateAppAsync(config);

        app.Use(async (ctx, next) =>
        {
            var supplied = ctx.Request.Headers["X-Test-Ip"].ToString();
            if (!string.IsNullOrEmpty(supplied))
            {
                ctx.Connection.RemoteIpAddress = System.Net.IPAddress.Parse(supplied);
            }
            await next(ctx);
        });
        app.UseApiPipeline(pipeline => pipeline.WithEdgeRateLimiting());
        app.MapGet("/test", () => Results.Ok("ok"));
        await app.StartAsync(TestContext.Current.CancellationToken);
        var client = app.GetTestClient();

        async Task<HttpStatusCode> Get(string ip)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "/test");
            request.Headers.Add("X-Test-Ip", ip);
            return (await client.SendAsync(request)).StatusCode;
        }

        (await Get("2001:db8:1:1::1")).Should().Be(HttpStatusCode.OK);
        (await Get("2001:db8:1:2::2")).Should().Be(HttpStatusCode.OK);
        ((int)await Get("2001:db8:1:3::3")).Should().Be(429,
            "all three addresses are inside one /48 and share a bucket");
    }

    /// <summary>
    /// Defaults are deliberately generous, because before authentication there is no way to tell a
    /// legitimate user from an attacker and NAT'd clients share an address.
    /// </summary>
    [Fact]
    public void EdgeRateLimitingOptions_Defaults_AreOffAndGenerous()
    {
        var options = new ApiPipelineEdgeRateLimitingOptions();

        options.Enabled.Should().BeFalse();
        options.PermitLimit.Should().Be(600);
        options.Window.Should().Be(TimeSpan.FromSeconds(60));

        // Null, not ["/health"]: the configuration binder appends to a populated collection, so a
        // pre-populated default could only ever be added to and /health could never be removed
        // from the exclusion list. The effective default lives in DefaultExcludedPaths and is
        // applied by the middleware when nothing is configured.
        options.ExcludedPaths.Should().BeNull();
        ApiPipelineEdgeRateLimitingOptions.DefaultExcludedPaths.Should().Equal("/health", "/internal/health");
    }

    /// <summary>
    /// Edge rejections go to <c>apipipeline.edge_rate_limiting.rejected</c>, not the main limiter's
    /// counter; ASP.NET Core emits no metric for this path, so this is its only signal.
    /// </summary>
    [Fact]
    public async Task EdgeRateLimiting_Rejection_RecordsItsOwnInstrument_NotTheSharedOne()
    {
        await using var app = await TestAppBuilder.CreateAppAsync(EdgeConfig(1));
        using var recorder = new MetricRecorder<long>(app.Services);
        app.Use(async (ctx, next) =>
        {
            ctx.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.9");
            await next(ctx);
        });
        app.UseApiPipeline(pipeline => pipeline.WithEdgeRateLimiting());
        app.MapGet("/test", () => Results.Ok("ok"));
        await app.StartAsync(TestContext.Current.CancellationToken);
        var client = app.GetTestClient();

        (await client.GetAsync("/test", TestContext.Current.CancellationToken)).StatusCode.Should().Be(HttpStatusCode.OK);
        ((int)(await client.GetAsync("/test", TestContext.Current.CancellationToken)).StatusCode).Should().Be(429);

        recorder.For("apipipeline.edge_rate_limiting.rejected").Sum(m => m.Value).Should().Be(1);
        recorder.For("apipipeline.rate_limiting.rejected").Should().BeEmpty(
            "apipipeline.rate_limiting.rejected covers the main limiter, whose rejections ASP.NET Core " +
            "already counts in aspnetcore.rate_limiting.requests");
    }

    /// <summary>
    /// The edge limiter's partitioned limiter runs a replenishment timer until it is disposed.
    /// Middleware is never disposed, so the limiter belongs to the host's container, which disposes
    /// it with the host: no timer outlives a host.
    /// </summary>
    [Fact]
    public async Task EdgeLimiter_IsDisposedWithTheHost()
    {
        var app = await TestAppBuilder.CreateAppAsync(EdgeConfig(5));
        app.UseApiPipeline(pipeline => pipeline.WithEdgeRateLimiting());
        app.MapGet("/test", () => Results.Ok("ok"));
        await app.StartAsync(TestContext.Current.CancellationToken);
        await app.GetTestClient().GetAsync("/test", TestContext.Current.CancellationToken);
        var limiter = app.Services.GetRequiredService<EdgeRateLimiter>();

        await app.DisposeAsync();

        var acquire = () => limiter.AttemptAcquire(new DefaultHttpContext());
        acquire.Should().Throw<ObjectDisposedException>("the host disposed the limiter and its timer");
    }

    /// <summary>
    /// Both limiters write the same 429, quota headers included, though the edge limiter bypasses
    /// <c>RateLimiterOptions.OnRejected</c>.
    /// </summary>
    [Fact]
    public async Task EdgeRejection_CarriesTheSameQuotaHeadersAsTheMainLimiter()
    {
        await using var app = await TestAppBuilder.CreateAppAsync(EdgeConfig(1, emitOnRejection: true));

        var rejected = await RejectFromEdgeAsync(app);

        ((int)rejected.StatusCode).Should().Be(429);
        RateLimitHeaders.PolicyValues(rejected).Should().Equal("\"edge\";q=1;w=600");
        RateLimitHeaders.Status(rejected).Should().Be("\"edge\";r=0;t=600");
    }

    /// <summary>
    /// And the switch that withholds them applies to this limiter too — it is the one that rejects
    /// before authentication, so it is the one where disclosing a quota to an anonymous caller
    /// matters most.
    /// </summary>
    [Fact]
    public async Task EdgeRejection_OmitsQuotaHeaders_WhenEmitOnRejectionIsOff()
    {
        await using var app = await TestAppBuilder.CreateAppAsync(
            EdgeConfig(1, emitOnRejection: false));

        var rejected = await RejectFromEdgeAsync(app);

        ((int)rejected.StatusCode).Should().Be(429);
        rejected.Headers.Contains(RateLimitHeaders.PolicyHeader).Should().BeFalse();
        rejected.Headers.Contains(RateLimitHeaders.StatusHeader).Should().BeFalse();
        rejected.Headers.Contains("Retry-After").Should().BeTrue(
            "Retry-After discloses the window, not the quota, and a client needs it to back off");
    }

}
