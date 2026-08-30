using System.Net;
using ApiPipeline.NET.Pipeline;
using FluentAssertions;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Options;

namespace ApiPipeline.NET.Tests;

/// <summary>
/// A health probe answered with 429 makes Kubernetes restart a healthy pod. Probes come from the
/// node's kubelet, so every probe of every replica on a node shares one client address — with
/// shared (Redis) limits, one bucket across the whole node. Probe paths are therefore never rate
/// limited by default, by the main limiter as well as the edge limiter.
/// </summary>
public sealed class HealthProbeRateLimitingTests
{
    /// <summary>The probe paths the package exempts out of the box, at both limiters.</summary>
    public static TheoryData<string> ProbePaths =>
    [
        "/health",
        "/health/ready",
        "/internal/health/liveness",
        "/internal/health/readiness",
        "/internal/health/startup",
    ];

    /// <summary>With a one-request limit, probes still never get a 429.</summary>
    [Theory]
    [MemberData(nameof(ProbePaths))]
    public async Task RateLimiting_DefaultProbePaths_AreNotLimited(string path)
    {
        await using var app = await TestAppBuilder.CreateAppAsync(TestAppBuilder.WithRateLimiting(permitLimit: 1));
        app.UseApiPipeline(pipeline => pipeline.WithRateLimiting());
        app.MapGet(path, () => Results.Ok("healthy"));
        await app.StartAsync(TestContext.Current.CancellationToken);
        var client = app.GetTestClient();

        for (var i = 0; i < 5; i++)
        {
            (await client.GetAsync(path, TestContext.Current.CancellationToken)).StatusCode
                .Should().Be(HttpStatusCode.OK, $"{path} is a probe path and must never be rate limited");
        }
    }

    /// <summary>The exemption is by path segment: a look-alike path is still limited.</summary>
    [Fact]
    public async Task RateLimiting_PathSharingOnlyAPrefix_IsLimited()
    {
        await using var app = await TestAppBuilder.CreateAppAsync(TestAppBuilder.WithRateLimiting(permitLimit: 1));
        app.UseApiPipeline(pipeline => pipeline.WithRateLimiting());
        app.MapGet("/healthcare", () => Results.Ok("ok"));
        await app.StartAsync(TestContext.Current.CancellationToken);
        var client = app.GetTestClient();

        (await client.GetAsync("/healthcare", TestContext.Current.CancellationToken)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/healthcare", TestContext.Current.CancellationToken)).StatusCode.Should().Be((HttpStatusCode)429);
    }

    /// <summary>A configured list replaces the default, so an application can name its own probes.</summary>
    [Fact]
    public async Task RateLimiting_ConfiguredExcludedPaths_ReplaceTheDefault()
    {
        var config = TestAppBuilder.WithRateLimiting(permitLimit: 1);
        config["ApiPipeline:RateLimiting:ExcludedPaths:0"] = "/probe";
        await using var app = await TestAppBuilder.CreateAppAsync(config);
        app.UseApiPipeline(pipeline => pipeline.WithRateLimiting());
        app.MapGet("/probe", () => Results.Ok("healthy"));
        app.MapGet("/health", () => Results.Ok("healthy"));
        await app.StartAsync(TestContext.Current.CancellationToken);
        var client = app.GetTestClient();

        for (var i = 0; i < 3; i++)
        {
            (await client.GetAsync("/probe", TestContext.Current.CancellationToken)).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        (await client.GetAsync("/health", TestContext.Current.CancellationToken)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/health", TestContext.Current.CancellationToken)).StatusCode.Should().Be(
            (HttpStatusCode)429, "the configured list replaced the default, so /health is no longer exempt");
    }

    /// <summary>
    /// Null, not the default list: the configuration binder appends to a populated collection, so a
    /// pre-filled default could never be removed. The effective default is a separate static.
    /// </summary>
    [Fact]
    public void RateLimitingOptions_ExcludedPaths_DefaultsToNull_WithProbeDefaults()
    {
        new ApiPipelineRateLimitingOptions().ExcludedPaths.Should().BeNull();
        ApiPipelineRateLimitingOptions.DefaultExcludedPaths.Should().Equal("/health", "/internal/health");
    }

    /// <summary>A path without a leading '/' can never match a request, so it fails startup.</summary>
    [Fact]
    public async Task RateLimiting_ExcludedPathWithoutLeadingSlash_FailsValidation()
    {
        var config = TestAppBuilder.WithRateLimiting();
        config["ApiPipeline:RateLimiting:ExcludedPaths:0"] = "health";
        await using var app = await TestAppBuilder.CreateAppAsync(config);

        var act = () => app.Services.GetRequiredService<IOptions<ApiPipelineRateLimitingOptions>>().Value;

        act.Should().Throw<OptionsValidationException>().WithMessage("*ExcludedPaths*'health'*");
    }

    /// <summary>The edge limiter exempts the same probe paths by default.</summary>
    [Theory]
    [MemberData(nameof(ProbePaths))]
    public async Task EdgeRateLimiting_DefaultProbePaths_AreNotLimited(string path)
    {
        var config = TestAppBuilder.MinimalConfig(c =>
        {
            c["ApiPipeline:EdgeRateLimiting:Enabled"] = "true";
            c["ApiPipeline:EdgeRateLimiting:PermitLimit"] = "1";
            c["ApiPipeline:EdgeRateLimiting:Window"] = "00:01:00";
        });
        await using var app = await TestAppBuilder.CreateAppAsync(config);
        app.Use((context, next) =>
        {
            // TestServer sets no peer address; the edge limiter skips a request without one.
            context.Connection.RemoteIpAddress = IPAddress.Parse("10.1.2.3");
            return next(context);
        });
        app.UseApiPipeline(pipeline => pipeline.WithEdgeRateLimiting());
        app.MapGet(path, () => Results.Ok("healthy"));
        await app.StartAsync(TestContext.Current.CancellationToken);
        var client = app.GetTestClient();

        for (var i = 0; i < 5; i++)
        {
            (await client.GetAsync(path, TestContext.Current.CancellationToken)).StatusCode
                .Should().Be(HttpStatusCode.OK, $"{path} is a probe path and must never be shed");
        }
    }

    /// <summary>
    /// A kubelet probes the pod directly. A probe-path request that came through a proxy (it
    /// carries <c>X-Forwarded-For</c>, or <c>X-Original-For</c> once forwarded headers have been
    /// applied) is from someone else, for example a caller hitting <c>/health</c> through the
    /// ingress to load the readiness checks' dependencies, and is limited like any other path.
    /// </summary>
    [Theory]
    [InlineData("X-Forwarded-For")]
    [InlineData("X-Original-For")]
    public async Task RateLimiting_ProbeThroughAProxy_IsLimited(string proxyHeader)
    {
        await using var app = await TestAppBuilder.CreateAppAsync(TestAppBuilder.WithRateLimiting(permitLimit: 1));
        app.UseApiPipeline(pipeline => pipeline.WithRateLimiting());
        app.MapGet("/health", () => Results.Ok("healthy"));
        await app.StartAsync(TestContext.Current.CancellationToken);
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add(proxyHeader, "198.51.100.7");

        (await client.GetAsync("/health", TestContext.Current.CancellationToken)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/health", TestContext.Current.CancellationToken)).StatusCode.Should().Be((HttpStatusCode)429);
    }

    /// <summary>The edge limiter applies the same rule.</summary>
    [Fact]
    public async Task EdgeRateLimiting_ProbeThroughAProxy_IsLimited()
    {
        var config = TestAppBuilder.MinimalConfig(c =>
        {
            c["ApiPipeline:EdgeRateLimiting:Enabled"] = "true";
            c["ApiPipeline:EdgeRateLimiting:PermitLimit"] = "1";
            c["ApiPipeline:EdgeRateLimiting:Window"] = "00:01:00";
        });
        await using var app = await TestAppBuilder.CreateAppAsync(config);
        app.Use((context, next) =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Parse("10.1.2.3");
            return next(context);
        });
        app.UseApiPipeline(pipeline => pipeline.WithEdgeRateLimiting());
        app.MapGet("/health", () => Results.Ok("healthy"));
        await app.StartAsync(TestContext.Current.CancellationToken);
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Forwarded-For", "198.51.100.7");

        (await client.GetAsync("/health", TestContext.Current.CancellationToken)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/health", TestContext.Current.CancellationToken)).StatusCode.Should().Be((HttpStatusCode)429);
    }

    /// <summary>
    /// An endpoint under a probe path that asks for a policy (<c>RequireRateLimiting</c>,
    /// <c>[EnableRateLimiting]</c>) gets it: the exemption is a default for probes, not a reason to
    /// silently ignore an explicit request.
    /// </summary>
    [Fact]
    public async Task RateLimiting_EndpointUnderAProbePath_ThatRequiresRateLimiting_IsLimited()
    {
        await using var app = await TestAppBuilder.CreateAppAsync(TestAppBuilder.WithRateLimiting(permitLimit: 1));
        app.UseApiPipeline(pipeline => pipeline.WithRateLimiting());
        app.MapGet("/health/details", () => Results.Ok("details")).RequireRateLimiting("strict");
        await app.StartAsync(TestContext.Current.CancellationToken);
        var client = app.GetTestClient();

        (await client.GetAsync("/health/details", TestContext.Current.CancellationToken)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/health/details", TestContext.Current.CancellationToken)).StatusCode.Should().Be((HttpStatusCode)429);
    }

    /// <summary>
    /// HTTP probes send GET (kubelet) or HEAD (some load balancers). Any other method under a probe
    /// path is an application endpoint (POST /health/reset, DELETE /health/cache) and is limited at
    /// both limiters; HEAD stays exempt.
    /// </summary>
    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public async Task RateLimiting_NonProbeMethodOnAProbePath_IsLimited(string method)
    {
        await using var app = await TestAppBuilder.CreateAppAsync(TestAppBuilder.WithRateLimiting(permitLimit: 1));
        app.UseApiPipeline(pipeline => pipeline.WithRateLimiting());
        app.MapMethods("/health/reset", [method], () => Results.Ok("reset"));
        await app.StartAsync(TestContext.Current.CancellationToken);
        var client = app.GetTestClient();

        HttpRequestMessage Request() => new(new HttpMethod(method), "/health/reset");
        (await client.SendAsync(Request(), TestContext.Current.CancellationToken)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.SendAsync(Request(), TestContext.Current.CancellationToken)).StatusCode.Should().Be((HttpStatusCode)429);
    }

    /// <summary>HEAD is a probe method, like GET.</summary>
    [Fact]
    public async Task RateLimiting_HeadProbe_IsNotLimited()
    {
        await using var app = await TestAppBuilder.CreateAppAsync(TestAppBuilder.WithRateLimiting(permitLimit: 1));
        app.UseApiPipeline(pipeline => pipeline.WithRateLimiting());
        app.MapMethods("/health", ["HEAD"], () => Results.Ok());
        await app.StartAsync(TestContext.Current.CancellationToken);
        var client = app.GetTestClient();

        for (var i = 0; i < 3; i++)
        {
            (await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/health"), TestContext.Current.CancellationToken))
                .StatusCode.Should().Be(HttpStatusCode.OK);
        }
    }

    /// <summary>The edge limiter applies the same method rule.</summary>
    [Fact]
    public async Task EdgeRateLimiting_NonProbeMethodOnAProbePath_IsLimited()
    {
        var config = TestAppBuilder.MinimalConfig(c =>
        {
            c["ApiPipeline:EdgeRateLimiting:Enabled"] = "true";
            c["ApiPipeline:EdgeRateLimiting:PermitLimit"] = "1";
            c["ApiPipeline:EdgeRateLimiting:Window"] = "00:01:00";
        });
        await using var app = await TestAppBuilder.CreateAppAsync(config);
        app.Use((context, next) =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Parse("10.1.2.3");
            return next(context);
        });
        app.UseApiPipeline(pipeline => pipeline.WithEdgeRateLimiting());
        app.MapPost("/health/reset", () => Results.Ok("reset"));
        await app.StartAsync(TestContext.Current.CancellationToken);
        var client = app.GetTestClient();

        (await client.PostAsync("/health/reset", null, TestContext.Current.CancellationToken)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.PostAsync("/health/reset", null, TestContext.Current.CancellationToken)).StatusCode.Should().Be((HttpStatusCode)429);
    }

    /// <summary>The edge limiter's default list is the same probe list.</summary>
    [Fact]
    public void EdgeRateLimitingOptions_DefaultExcludedPaths_AreTheProbePaths()
    {
        ApiPipelineEdgeRateLimitingOptions.DefaultExcludedPaths.Should().Equal("/health", "/internal/health");
    }
}
