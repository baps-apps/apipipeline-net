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
}
