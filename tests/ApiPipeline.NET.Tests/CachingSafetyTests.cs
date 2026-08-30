using System.Net;
using System.Security.Claims;
using ApiPipeline.NET.Pipeline;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.AspNetCore.TestHost;

namespace ApiPipeline.NET.Tests;

/// <summary>
/// What the caches must never do: store authenticated responses, or replay one origin's CORS
/// headers to another. Both depend on phase order, so a reordering would break them silently.
/// </summary>
public sealed class CachingSafetyTests
{
    /// <summary>
    /// An authenticated response never enters the output cache. Relies on authentication running
    /// before output caching.
    /// </summary>
    [Fact]
    public async Task OutputCaching_AuthenticatedPrincipal_ResponseIsNotCached()
    {
        var config = TestAppBuilder.MinimalConfig(c => c["ApiPipeline:OutputCaching:Enabled"] = "true");
        await using var app = await TestAppBuilder.CreateAppAsync(config);

        app.UseApiPipeline(pipeline => pipeline
            .WithAuthentication()
            .WithAuthorization()
            .WithOutputCaching());

        app.MapGet("/me", (HttpContext ctx) =>
                Results.Ok((ctx.User.Identity?.IsAuthenticated == true ? "auth" : "anon") + ":" + Guid.NewGuid()))
            .CacheOutput();

        await app.StartAsync(TestContext.Current.CancellationToken);
        var client = app.GetTestClient();

        var first = new HttpRequestMessage(HttpMethod.Get, "/me");
        first.Headers.Add("Authorization", "Bearer valid-token");
        var authBody = await (await client.SendAsync(first, TestContext.Current.CancellationToken)).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        var second = new HttpRequestMessage(HttpMethod.Get, "/me");
        second.Headers.Add("Authorization", "Bearer valid-token");
        var authBody2 = await (await client.SendAsync(second, TestContext.Current.CancellationToken)).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        authBody.Should().NotBe(authBody2,
            "an authenticated response must not be stored and replayed");

        var anonBody = await (await client.GetAsync("/me", TestContext.Current.CancellationToken)).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        anonBody.Should().NotBe(authBody,
            "an anonymous caller must never receive the authenticated response");
    }

    /// <summary>
    /// Anonymous responses are still cached — the protection above must not be achieved by
    /// disabling caching altogether.
    /// </summary>
    [Fact]
    public async Task OutputCaching_AnonymousResponse_IsCached()
    {
        var config = TestAppBuilder.MinimalConfig(c => c["ApiPipeline:OutputCaching:Enabled"] = "true");
        await using var app = await TestAppBuilder.CreateAppAsync(config);

        app.UseApiPipeline(pipeline => pipeline.WithOutputCaching());
        app.MapGet("/public", () => Results.Ok(Guid.NewGuid().ToString())).CacheOutput();

        await app.StartAsync(TestContext.Current.CancellationToken);
        var client = app.GetTestClient();

        var first = await (await client.GetAsync("/public", TestContext.Current.CancellationToken)).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var second = await (await client.GetAsync("/public", TestContext.Current.CancellationToken)).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        second.Should().Be(first, "an anonymous cacheable response should be served from the cache");
    }

    /// <summary>
    /// A response cached for one origin must not hand that origin's
    /// <c>Access-Control-Allow-Origin</c> to a different origin. CORS runs before output caching in
    /// the phase order, so the header is recomputed per request even on a cache hit.
    /// </summary>
    [Fact]
    public async Task OutputCaching_DifferentOrigins_DoNotShareAccessControlAllowOrigin()
    {
        var config = TestAppBuilder.MinimalConfig(c =>
        {
            c["ApiPipeline:OutputCaching:Enabled"] = "true";
            c["ApiPipeline:Cors:Enabled"] = "true";
            c["ApiPipeline:Cors:AllowedOrigins:0"] = "https://a.example";
            c["ApiPipeline:Cors:AllowedOrigins:1"] = "https://b.example";
        });
        await using var app = await TestAppBuilder.CreateAppAsync(config);

        app.UseApiPipeline(pipeline => pipeline.WithCors().WithOutputCaching());
        app.MapGet("/data", () => Results.Ok(Guid.NewGuid().ToString())).CacheOutput();

        await app.StartAsync(TestContext.Current.CancellationToken);
        var client = app.GetTestClient();

        var fromA = new HttpRequestMessage(HttpMethod.Get, "/data");
        fromA.Headers.Add("Origin", "https://a.example");
        var responseA = await client.SendAsync(fromA, TestContext.Current.CancellationToken);

        var fromB = new HttpRequestMessage(HttpMethod.Get, "/data");
        fromB.Headers.Add("Origin", "https://b.example");
        var responseB = await client.SendAsync(fromB, TestContext.Current.CancellationToken);

        responseA.Headers.GetValues("Access-Control-Allow-Origin").Single()
            .Should().Be("https://a.example");
        responseB.Headers.GetValues("Access-Control-Allow-Origin").Single()
            .Should().Be("https://b.example",
                "the cached body may be reused, but the CORS header must be recomputed per origin");
    }

    /// <summary>
    /// Response caching keys on the <c>Authorization</c> header, which cookie-authenticated APIs do
    /// not send. This test documents that the pipeline does not turn a cookie-authenticated
    /// endpoint into a cross-user leak on its own: caching still requires the application to opt in
    /// with public cache headers, and this endpoint does not.
    /// </summary>
    [Fact]
    public async Task ResponseCaching_CookieAuthenticatedResponse_IsNotServedToAnotherUser()
    {
        var config = TestAppBuilder.MinimalConfig(c => c["ApiPipeline:ResponseCaching:Enabled"] = "true");
        await using var app = await TestAppBuilder.CreateAppAsync(config);

        // Stand in for cookie authentication: an identity with no Authorization header.
        app.Use(async (ctx, next) =>
        {
            var user = ctx.Request.Headers["X-Test-User"].ToString();
            if (!string.IsNullOrEmpty(user))
            {
                ctx.User = new ClaimsPrincipal(
                    new ClaimsIdentity([new Claim(ClaimTypes.Name, user)], "Cookies"));
            }
            await next(ctx);
        });
        app.UseApiPipeline(pipeline => pipeline.WithResponseCaching());

        app.MapGet("/me", (HttpContext ctx) => Results.Ok("secret-for:" + (ctx.User.Identity?.Name ?? "anon")))
            .WithMetadata(new ResponseCacheAttribute { Duration = 60 });

        await app.StartAsync(TestContext.Current.CancellationToken);
        var client = app.GetTestClient();

        var alice = new HttpRequestMessage(HttpMethod.Get, "/me");
        alice.Headers.Add("X-Test-User", "alice");
        var aliceBody = await (await client.SendAsync(alice, TestContext.Current.CancellationToken)).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        var bob = new HttpRequestMessage(HttpMethod.Get, "/me");
        bob.Headers.Add("X-Test-User", "bob");
        var bobBody = await (await client.SendAsync(bob, TestContext.Current.CancellationToken)).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        aliceBody.Should().Contain("alice");
        bobBody.Should().Contain("bob");
        bobBody.Should().NotBe(aliceBody, "one user must never be served another user's response");
    }

    /// <summary>
    /// Output caching is the modern mechanism and the one the package steers teams towards; enabling
    /// both at once is rejected at startup, so this asserts the recommended combination works.
    /// </summary>
    [Fact]
    public async Task OutputCaching_WithResponseCachingDisabled_StartsAndCaches()
    {
        var config = TestAppBuilder.MinimalConfig(c =>
        {
            c["ApiPipeline:OutputCaching:Enabled"] = "true";
            c["ApiPipeline:ResponseCaching:Enabled"] = "false";
        });
        await using var app = await TestAppBuilder.CreateAppAsync(config);

        app.UseApiPipeline(pipeline => pipeline.WithResponseCaching().WithOutputCaching());
        app.MapGet("/x", () => Results.Ok(Guid.NewGuid().ToString())).CacheOutput();

        await app.StartAsync(TestContext.Current.CancellationToken);
        var client = app.GetTestClient();

        var first = await (await client.GetAsync("/x", TestContext.Current.CancellationToken)).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var second = await (await client.GetAsync("/x", TestContext.Current.CancellationToken)).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        second.Should().Be(first);
    }
}
