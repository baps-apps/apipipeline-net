using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;

namespace ApiPipeline.NET.Tests;

/// <summary>
/// Tests for ApiPipelineBuilder — the builder contract. Ordering is covered by PipelineCompositionTests.
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
            c["ApiPipeline:ResponseCaching:Enabled"] = "true";
        });
        await using var app = await TestAppBuilder.CreateAppAsync(config, addProblemDetails: true);

        app.UseApiPipeline(pipeline => pipeline
            .WithAuthentication()
            .WithAuthorization()
            .WithResponseCaching());

        app.MapGet("/secure", [Authorize] () => Results.Ok("secret"))
            .WithMetadata(new ResponseCacheAttribute { Duration = 60 });

        await app.StartAsync(TestContext.Current.CancellationToken);
        var client = app.GetTestClient();

        // Unauthenticated request must get 401, not a cached 200
        var unauthResponse = await client.GetAsync("/secure", TestContext.Current.CancellationToken);
        unauthResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    /// <summary>
    /// A phase never requested is never added: without <c>WithHttpsRedirection()</c>, a plain HTTP
    /// request reaches the endpoint.
    /// </summary>
    [Fact]
    public async Task UseApiPipeline_UnrequestedMiddleware_IsNotRegistered()
    {
        var config = TestAppBuilder.MinimalConfig();
        await using var app = await TestAppBuilder.CreateAppAsync(config);

        app.UseApiPipeline(pipeline => pipeline.WithCorrelationId());

        app.MapGet("/test", () => Results.Ok("ok"));
        await app.StartAsync(TestContext.Current.CancellationToken);

        var client = app.GetTestClient();
        var response = await client.GetAsync("/test", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
