using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.TestHost;

namespace ApiPipeline.NET.Tests;

/// <summary>
/// <c>UseApiPipeline</c> calls <c>UseRouting()</c> itself, right after forwarded headers, in the
/// order Microsoft documents for ASP.NET Core middleware. Every endpoint-aware phase (CORS, rate
/// limiting, request timeouts, request-size telemetry, output caching) therefore sees the selected
/// endpoint however the application orders its own calls.
/// </summary>
public sealed class PipelineRoutingTests
{
    private static Dictionary<string, string?> NamedCorsConfig() =>
        TestAppBuilder.MinimalConfig(c =>
        {
            c["ApiPipeline:Cors:Enabled"] = "true";
            c["ApiPipeline:Cors:AllowedOrigins:0"] = "https://pipeline.example";
        });

    /// <summary>
    /// A <c>UseRouting()</c> after the pipeline is a no-op: the pipeline already selected the endpoint,
    /// so <c>[EnableCors("name")]</c> still applies.
    /// </summary>
    [Fact]
    public async Task UseRouting_AfterUseApiPipeline_EndpointMetadataStillApplies()
    {
        await using var app = await TestAppBuilder.CreateAppAsync(
            NamedCorsConfig(),
            configureServices: s => s.AddCors(o => o.AddPolicy(
                "partner", p => p.WithOrigins("https://partner.example"))));
        app.UseApiPipeline(pipeline => pipeline.WithCors());
        app.UseRouting();
        app.MapGet("/test", () => "ok").RequireCors("partner");
        await app.StartAsync(TestContext.Current.CancellationToken);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/test");
        request.Headers.Add("Origin", "https://partner.example");
        var response = await app.GetTestClient().SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.GetValues("Access-Control-Allow-Origin").Should()
            .ContainSingle("https://partner.example",
                "the endpoint's named policy applies because the pipeline routed before CORS");
    }

    /// <summary>
    /// The common case, no explicit <c>UseRouting</c>: endpoint metadata is visible to the phases.
    /// </summary>
    [Fact]
    public async Task NoExplicitUseRouting_EndpointMetadataIsVisible()
    {
        await using var app = await TestAppBuilder.CreateAppAsync(TestAppBuilder.MinimalConfig());
        app.UseApiPipeline(pipeline => pipeline.WithCors().WithRateLimiting());
        app.MapGet("/test", (HttpContext ctx) =>
            Results.Ok(ctx.GetEndpoint() is null ? "no-endpoint" : "endpoint"));
        await app.StartAsync(TestContext.Current.CancellationToken);

        var body = await app.GetTestClient().GetStringAsync("/test", TestContext.Current.CancellationToken);

        body.Should().Contain("endpoint");
    }

    /// <summary>
    /// An application that already routes before the pipeline keeps working: the pipeline's routing
    /// step skips a request whose endpoint is already selected.
    /// </summary>
    [Fact]
    public async Task UseRouting_BeforeUseApiPipeline_StillWorks()
    {
        await using var app = await TestAppBuilder.CreateAppAsync(TestAppBuilder.MinimalConfig());
        app.UseRouting();
        app.UseApiPipeline(pipeline => pipeline.WithCors().WithRateLimiting());
        app.MapGet("/test", () => "ok");
        await app.StartAsync(TestContext.Current.CancellationToken);

        var response = await app.GetTestClient().GetAsync("/test", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
