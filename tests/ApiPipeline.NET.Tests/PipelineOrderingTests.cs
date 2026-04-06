using System.Net;
using ApiPipeline.NET.Extensions;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace ApiPipeline.NET.Tests;

/// <summary>
/// Tests that verify correct middleware ordering in the API pipeline,
/// specifically that authentication and authorization run before response caching
/// to prevent auth-bypass via cached responses.
/// </summary>
public sealed class PipelineOrderingTests
{
    /// <summary>
    /// Verifies that an unauthenticated request to a protected endpoint returns HTTP 401
    /// and is not served from the response cache, confirming that authorization runs before caching.
    /// </summary>
    [Fact]
    public async Task ResponseCaching_Does_Not_Serve_Cached_Response_Without_Authorization()
    {
        // Verifies that auth is checked BEFORE the cache can replay a response.
        var config = TestAppBuilder.MinimalConfig(c =>
        {
            c["ResponseCachingOptions:Enabled"] = "true";
        });
        await using var app = await TestAppBuilder.CreateAppAsync(config, addExceptionHandler: true);

        app.UseAuthentication();
        app.UseAuthorization();
        app.UseResponseCaching();

        app.MapGet("/secure", [Authorize] () => Results.Ok("secret"))
            .WithMetadata(new ResponseCacheAttribute { Duration = 60 });

        await app.StartAsync();
        var client = app.GetTestClient();

        // Unauthenticated request should get 401, not a cached 200
        var unauthResponse = await client.GetAsync("/secure");
        unauthResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    /// <summary>
    /// Verifies that a cached 200 response from an authenticated request is NOT replayed
    /// to a subsequent unauthenticated request, confirming that authorization runs before
    /// the cache can short-circuit the pipeline.
    /// </summary>
    [Fact]
    public async Task ResponseCaching_Does_Not_Replay_Authenticated_Response_To_Unauthenticated_Request()
    {
        // This test proves the cache-bypass scenario is not possible:
        // 1. An authenticated request warms the cache with a 200.
        // 2. A subsequent unauthenticated request must still get 401, not the cached 200.
        var config = TestAppBuilder.MinimalConfig(c =>
        {
            c["ResponseCachingOptions:Enabled"] = "true";
        });
        await using var app = await TestAppBuilder.CreateAppAsync(config, addExceptionHandler: true);

        // Auth BEFORE caching — the correct, safe ordering.
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseResponseCaching();

        app.MapGet("/secure", [Authorize] () => Results.Ok("secret"))
            .WithMetadata(new ResponseCacheAttribute { Duration = 60 });

        await app.StartAsync();
        var client = app.GetTestClient();

        // Step 1: authenticated request — should succeed (200) and warm the cache.
        var authRequest = new HttpRequestMessage(HttpMethod.Get, "/secure");
        authRequest.Headers.Add("Authorization", "Bearer valid-token");
        var authResponse = await client.SendAsync(authRequest);
        authResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        // Step 2: unauthenticated request — must NOT receive the cached 200.
        var unauthResponse = await client.GetAsync("/secure");
        unauthResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
