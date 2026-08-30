using System.Net;
using ApiPipeline.NET.Pipeline;
using FluentAssertions;
using Microsoft.AspNetCore.TestHost;

namespace ApiPipeline.NET.Tests;

/// <summary>
/// <c>Cache-Control: no-store</c> is the default for API responses (OWASP REST Security Cheat Sheet),
/// applied only when the response sets no <c>Cache-Control</c> of its own. An endpoint that opts in
/// to caching sets its own value, which always wins, so caching keeps working.
/// </summary>
public sealed class SecurityHeadersCacheControlTests
{
    /// <summary>Fails if the default is not applied to a response that sets no Cache-Control.</summary>
    [Fact]
    public async Task ResponseWithoutCacheControl_GetsNoStore_ByDefault()
    {
        await using var app = await TestAppBuilder.CreateAppAsync(TestAppBuilder.WithSecurityHeaders());
        app.UseApiPipeline(pipeline => pipeline.WithSecurityHeaders());
        app.MapGet("/account", () => Results.Ok("sensitive"));
        await app.StartAsync(TestContext.Current.CancellationToken);

        var response = await app.GetTestClient().GetAsync("/account", TestContext.Current.CancellationToken);

        response.Headers.CacheControl.Should().NotBeNull();
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
    }

    /// <summary>An endpoint's own Cache-Control is never overwritten.</summary>
    [Fact]
    public async Task EndpointCacheControl_IsNotOverwritten()
    {
        await using var app = await TestAppBuilder.CreateAppAsync(TestAppBuilder.WithSecurityHeaders());
        app.UseApiPipeline(pipeline => pipeline.WithSecurityHeaders());
        app.MapGet("/catalog", (HttpContext ctx) =>
        {
            ctx.Response.Headers.CacheControl = "public, max-age=60";
            return Results.Ok("public data");
        });
        await app.StartAsync(TestContext.Current.CancellationToken);

        var response = await app.GetTestClient().GetAsync("/catalog", TestContext.Current.CancellationToken);

        response.Headers.CacheControl!.ToString().Should().Be("public, max-age=60");
    }

    /// <summary>An empty value turns the default off.</summary>
    [Fact]
    public async Task EmptyCacheControlOption_SendsNoCacheControl()
    {
        await using var app = await TestAppBuilder.CreateAppAsync(
            TestAppBuilder.WithSecurityHeaders().Also(c => c["ApiPipeline:SecurityHeaders:CacheControl"] = ""));
        app.UseApiPipeline(pipeline => pipeline.WithSecurityHeaders());
        app.MapGet("/account", () => Results.Ok("data"));
        await app.StartAsync(TestContext.Current.CancellationToken);

        var response = await app.GetTestClient().GetAsync("/account", TestContext.Current.CancellationToken);

        response.Headers.CacheControl.Should().BeNull();
    }

    /// <summary>
    /// The default must not break response caching: an endpoint that opts in with its own
    /// <c>Cache-Control: public</c> is still served from the cache.
    /// </summary>
    [Fact]
    public async Task ResponseCaching_StillCachesAnEndpointThatOptsIn()
    {
        var config = TestAppBuilder.WithSecurityHeaders().Also(c => c["ApiPipeline:ResponseCaching:Enabled"] = "true");
        await using var app = await TestAppBuilder.CreateAppAsync(config);
        app.UseApiPipeline(pipeline => pipeline.WithSecurityHeaders().WithResponseCaching());
        var executions = 0;
        app.MapGet("/catalog", (HttpContext ctx) =>
        {
            executions++;
            ctx.Response.Headers.CacheControl = "public, max-age=60";
            return Results.Ok("public data");
        });
        await app.StartAsync(TestContext.Current.CancellationToken);
        var client = app.GetTestClient();

        (await client.GetAsync("/catalog", TestContext.Current.CancellationToken)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/catalog", TestContext.Current.CancellationToken)).StatusCode.Should().Be(HttpStatusCode.OK);

        executions.Should().Be(1, "the second request is served from the response cache");
    }

    /// <summary>
    /// Output caching is server-side and does not read the response's <c>Cache-Control</c>, so an
    /// endpoint using <c>CacheOutput()</c> is still cached with the default applied.
    /// </summary>
    [Fact]
    public async Task OutputCaching_StillCachesWithTheDefaultApplied()
    {
        var config = TestAppBuilder.WithSecurityHeaders().Also(c => c["ApiPipeline:OutputCaching:Enabled"] = "true");
        await using var app = await TestAppBuilder.CreateAppAsync(config);
        app.UseApiPipeline(pipeline => pipeline.WithSecurityHeaders().WithOutputCaching());
        var executions = 0;
        app.MapGet("/report", () =>
        {
            executions++;
            return Results.Ok("report");
        }).CacheOutput();
        await app.StartAsync(TestContext.Current.CancellationToken);
        var client = app.GetTestClient();

        await client.GetAsync("/report", TestContext.Current.CancellationToken);
        var second = await client.GetAsync("/report", TestContext.Current.CancellationToken);

        executions.Should().Be(1, "the second request is served from the output cache");
        second.Headers.CacheControl!.NoStore.Should().BeTrue(
            "downstream caches still get the safe default; the server-side cache is unaffected");
    }
}
