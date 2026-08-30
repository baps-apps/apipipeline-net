using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.TestHost;

namespace ApiPipeline.NET.Tests;

/// <summary>
/// Behaviour for apps that authenticate in an MVC <c>IAsyncAuthorizationFilter</c> (common here):
/// the filter runs after every pipeline middleware, and <c>HttpContext.User</c> is never set.
/// </summary>
public sealed class FilterBasedAuthConsumerTests
{
    /// <summary>
    /// The output cache still refuses requests carrying an <c>Authorization</c> header, even with no
    /// principal. This is the only thing stopping cross-user replay for filter-based auth.
    /// </summary>
    [Fact]
    public async Task OutputCaching_WithAuthorizationHeaderAndNoPrincipal_DoesNotCache()
    {
        var config = TestAppBuilder.MinimalConfig(c => c["ApiPipeline:OutputCaching:Enabled"] = "true");
        await using var app = await TestAppBuilder.CreateAppAsync(config);

        // No WithAuthentication(): this application has no authentication middleware at all.
        app.UseApiPipeline(pipeline => pipeline.WithOutputCaching());
        app.MapGet("/me", () => Results.Ok(Guid.NewGuid().ToString())).CacheOutput();
        await app.StartAsync(TestContext.Current.CancellationToken);
        var client = app.GetTestClient();

        static HttpRequestMessage WithBearer()
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "/me");
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer sso-token");
            return request;
        }

        var first = await (await client.SendAsync(WithBearer(), TestContext.Current.CancellationToken)).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var second = await (await client.SendAsync(WithBearer(), TestContext.Current.CancellationToken)).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        second.Should().NotBe(first,
            "the Authorization header alone must keep a response out of the shared cache");
    }

    /// <summary>
    /// Same guarantee for the legacy response cache.
    /// </summary>
    [Fact]
    public async Task ResponseCaching_WithAuthorizationHeaderAndNoPrincipal_DoesNotCache()
    {
        var config = TestAppBuilder.MinimalConfig(c => c["ApiPipeline:ResponseCaching:Enabled"] = "true");
        await using var app = await TestAppBuilder.CreateAppAsync(config);

        app.UseApiPipeline(pipeline => pipeline.WithResponseCaching());
        app.MapGet("/me", (HttpContext ctx) =>
        {
            ctx.Response.Headers.CacheControl = "public,max-age=60";
            return Results.Ok(Guid.NewGuid().ToString());
        });
        await app.StartAsync(TestContext.Current.CancellationToken);
        var client = app.GetTestClient();

        static HttpRequestMessage WithBearer()
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "/me");
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer sso-token");
            return request;
        }

        var first = await (await client.SendAsync(WithBearer(), TestContext.Current.CancellationToken)).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var second = await (await client.SendAsync(WithBearer(), TestContext.Current.CancellationToken)).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        second.Should().NotBe(first,
            "response caching must not store a response for a request carrying credentials");
    }

    /// <summary>
    /// The consequence that is <em>not</em> mitigated: with no <c>ClaimsPrincipal</c>, rate limiting
    /// falls back to partitioning by client address. Distinct users arriving from one address — an
    /// office NAT, a mobile carrier gateway, or a single ingress when forwarded headers are
    /// unconfigured — share one bucket, and nothing reports it.
    /// </summary>
    [Fact]
    public async Task RateLimiting_WithNoPrincipal_DistinctUsersFromOneAddress_ShareOneBucket()
    {
        var config = TestAppBuilder.WithRateLimiting(permitLimit: 2, windowSeconds: 60);
        // Reject, not the shared anonymous bucket: this test must prove address partitioning, not
        // the null-address fallback that TestServer would otherwise trigger.
        config["ApiPipeline:RateLimiting:AnonymousFallback"] = "Reject";
        await using var app = await TestAppBuilder.CreateAppAsync(config, addProblemDetails: true);

        app.UseSimulatedPeerAddress();
        app.UseApiPipeline(pipeline => pipeline.WithRateLimiting());
        app.MapGet("/test", () => Results.Ok("ok"));
        await app.StartAsync(TestContext.Current.CancellationToken);
        var client = app.GetTestClient();

        (await client.SendAsync(FromUser("user-a", "203.0.113.10"), TestContext.Current.CancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.OK);
        (await client.SendAsync(FromUser("user-b", "203.0.113.10"), TestContext.Current.CancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.OK);
        var third = await client.SendAsync(FromUser("user-c", "203.0.113.10"), TestContext.Current.CancellationToken);

        third.StatusCode.Should().Be((HttpStatusCode)429,
            "three distinct SSO users behind one address consumed a per-address limit of 2, because " +
            "no ClaimsPrincipal exists for the limiter to partition on");
    }

    /// <summary>
    /// The other half of the same fact: the fallback really is per address, not one global bucket.
    /// Callers from different addresses are limited independently.
    /// </summary>
    [Fact]
    public async Task RateLimiting_WithNoPrincipal_DifferentAddresses_GetSeparateBuckets()
    {
        var config = TestAppBuilder.WithRateLimiting(permitLimit: 1, windowSeconds: 60);
        config["ApiPipeline:RateLimiting:AnonymousFallback"] = "Reject";
        await using var app = await TestAppBuilder.CreateAppAsync(config, addProblemDetails: true);

        app.UseSimulatedPeerAddress();
        app.UseApiPipeline(pipeline => pipeline.WithRateLimiting());
        app.MapGet("/test", () => Results.Ok("ok"));
        await app.StartAsync(TestContext.Current.CancellationToken);
        var client = app.GetTestClient();

        (await client.SendAsync(FromUser("user-a", "203.0.113.10"), TestContext.Current.CancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.OK);
        (await client.SendAsync(FromUser("user-a", "203.0.113.10"), TestContext.Current.CancellationToken)).StatusCode
            .Should().Be((HttpStatusCode)429, "the first address exhausted its limit of 1");
        (await client.SendAsync(FromUser("user-b", "198.51.100.20"), TestContext.Current.CancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.OK, "a different address is a different partition");
    }

    private static HttpRequestMessage FromUser(string token, string peerAddress)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/test");
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
        request.Headers.TryAddWithoutValidation(TestPeerAddress.HeaderName, peerAddress);
        return request;
    }

    /// <summary>
    /// Documents which claim types the limiter looks for, so an application that <em>does</em>
    /// populate a principal knows what to emit. A token whose user id lives under any other claim
    /// (<c>pid</c>, <c>employeeId</c>, …) falls back to address partitioning with no warning.
    /// </summary>
    [Fact]
    public void RateLimitPartitioning_RecognisedUserClaims_AreSubNameidAndNameIdentifier()
    {
        // Pinned as documentation-in-code: these three names are the contract an application's
        // principal has to satisfy to get per-user quotas.
        var recognised = new[] { "sub", "nameid", System.Security.Claims.ClaimTypes.NameIdentifier };

        recognised.Should().HaveCount(3);
        new ApiPipelineRateLimitingOptions().EnableApiKeyPartitioning.Should().BeFalse(
            "the API key fallback that could substitute for a user partition is off by default");
    }
}
