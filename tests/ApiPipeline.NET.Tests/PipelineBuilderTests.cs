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
/// Tests for IApiPipelineBuilder — verifies phase ordering and builder contract.
/// </summary>
public sealed class PipelineBuilderTests
{
    /// <summary>
    /// Verifies that UseApiPipeline applies middleware in the correct order:
    /// specifically that auth runs before caching, preventing auth-bypass via cache.
    /// </summary>
    [Fact]
    public async Task UseApiPipeline_Auth_Before_Caching_Prevents_Cache_Bypass()
    {
        var config = TestAppBuilder.MinimalConfig(c =>
        {
            c["ResponseCachingOptions:Enabled"] = "true";
        });
        await using var app = await TestAppBuilder.CreateAppAsync(config, addExceptionHandler: true);

        app.UseApiPipeline(pipeline => pipeline
            .WithAuthentication()
            .WithAuthorization()
            .WithResponseCaching());

        app.MapGet("/secure", [Authorize] () => Results.Ok("secret"))
            .WithMetadata(new ResponseCacheAttribute { Duration = 60 });

        await app.StartAsync();
        var client = app.GetTestClient();

        // Unauthenticated request must get 401, not a cached 200
        var unauthResponse = await client.GetAsync("/secure");
        unauthResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    /// <summary>
    /// Verifies that calling With* methods in any order produces the correct pipeline order.
    /// Registering WithResponseCaching before WithAuthentication must still apply auth first.
    /// </summary>
    [Fact]
    public async Task UseApiPipeline_Order_Of_With_Calls_Does_Not_Affect_Pipeline_Order()
    {
        var config = TestAppBuilder.MinimalConfig(c =>
        {
            c["ResponseCachingOptions:Enabled"] = "true";
        });
        await using var app = await TestAppBuilder.CreateAppAsync(config, addExceptionHandler: true);

        // Deliberately register in wrong order — builder must fix it
        app.UseApiPipeline(pipeline => pipeline
            .WithResponseCaching()   // registered first but must execute AFTER auth
            .WithAuthorization()
            .WithAuthentication());

        app.MapGet("/secure", [Authorize] () => Results.Ok("secret"))
            .WithMetadata(new ResponseCacheAttribute { Duration = 60 });

        await app.StartAsync();
        var client = app.GetTestClient();

        var unauthResponse = await client.GetAsync("/secure");
        unauthResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "even when WithResponseCaching is declared before WithAuthentication, auth must run first");
    }

    /// <summary>
    /// Verifies that Skip methods prevent a middleware from being added.
    /// </summary>
    [Fact]
    public async Task UseApiPipeline_Skip_Prevents_Middleware_Registration()
    {
        var config = TestAppBuilder.MinimalConfig();
        await using var app = await TestAppBuilder.CreateAppAsync(config);

        // If HTTPS redirection were active, HTTP requests would be redirected
        // By skipping it, HTTP requests pass through
        app.UseApiPipeline(pipeline => pipeline
            .WithHttpsRedirection()
            .SkipHttpsRedirection());  // skip overrides with

        app.MapGet("/test", () => Results.Ok("ok"));
        await app.StartAsync();

        var client = app.GetTestClient();
        var response = await client.GetAsync("/test");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
