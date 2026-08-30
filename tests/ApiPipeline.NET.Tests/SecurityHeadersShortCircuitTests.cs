using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.TestHost;

namespace ApiPipeline.NET.Tests;

/// <summary>
/// Security headers must be on short-circuited responses too (401/403, 429, CORS preflight 204,
/// application rejections), which requires the security-headers phase to run before them.
/// </summary>
public sealed class SecurityHeadersShortCircuitTests
{
    private static Dictionary<string, string?> Config(Action<Dictionary<string, string?>>? extra = null)
    {
        return TestAppBuilder.MinimalConfig(c =>
        {
            c["ApiPipeline:SecurityHeaders:Enabled"] = "true";
            c["ApiPipeline:ForwardedHeaders:Enabled"] = "false";
            extra?.Invoke(c);
        });
    }

    private static void AssertSecurityHeadersPresent(HttpResponseMessage response)
    {
        response.Headers.Contains("Strict-Transport-Security").Should()
            .BeTrue("HSTS must be applied to short-circuited responses");
        response.Headers.Contains("X-Content-Type-Options").Should()
            .BeTrue("nosniff must be applied to short-circuited responses");
    }

    /// <summary>
    /// A rate-limited 429 must still carry security headers.
    /// </summary>
    [Fact]
    public async Task SecurityHeaders_Present_On_429_RateLimited_Response()
    {
        var config = Config(c =>
        {
            c["ApiPipeline:RateLimiting:Enabled"] = "true";
            c["ApiPipeline:RateLimiting:DefaultPolicy"] = "strict";
            c["ApiPipeline:RateLimiting:Policies:0:Name"] = "strict";
            c["ApiPipeline:RateLimiting:Policies:0:Kind"] = "FixedWindow";
            c["ApiPipeline:RateLimiting:Policies:0:PermitLimit"] = "1";
            c["ApiPipeline:RateLimiting:Policies:0:Window"] = "00:01:00";
        });
        await using var app = await TestAppBuilder.CreateAppAsync(config, Environments.Production);

        app.UseSimulatedHttps();
        app.UseApiPipeline(p => p.WithSecurityHeaders().WithRateLimiting());
        app.MapGet("/limited", () => Results.Ok("ok"));
        await app.StartAsync(TestContext.Current.CancellationToken);

        var client = app.GetApiTestClient();
        await client.GetAsync("/limited", TestContext.Current.CancellationToken);
        var rejected = await client.GetAsync("/limited", TestContext.Current.CancellationToken);

        rejected.StatusCode.Should().Be((HttpStatusCode)429);
        AssertSecurityHeadersPresent(rejected);
    }

    /// <summary>
    /// A 401 challenge from authorization must still carry security headers.
    /// </summary>
    [Fact]
    public async Task SecurityHeaders_Present_On_401_Unauthenticated_Response()
    {
        await using var app = await TestAppBuilder.CreateAppAsync(Config(), Environments.Production);

        app.UseSimulatedHttps();
        app.UseApiPipeline(p => p.WithSecurityHeaders().WithAuthentication().WithAuthorization());
        app.MapGet("/secure", () => Results.Ok("ok")).RequireAuthorization();
        await app.StartAsync(TestContext.Current.CancellationToken);

        var response = await app.GetApiTestClient().GetAsync("/secure", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        AssertSecurityHeadersPresent(response);
    }

    /// <summary>
    /// A CORS preflight, which the CORS middleware answers and short-circuits, must still carry
    /// security headers.
    /// </summary>
    [Fact]
    public async Task SecurityHeaders_Present_On_CorsPreflight_Response()
    {
        var config = Config(c =>
        {
            c["ApiPipeline:Cors:Enabled"] = "true";
            c["ApiPipeline:Cors:AllowedOrigins:0"] = "https://allowed.example.com";
        });
        await using var app = await TestAppBuilder.CreateAppAsync(config, Environments.Production);

        app.UseSimulatedHttps();
        app.UseApiPipeline(p => p.WithSecurityHeaders().WithCors());
        app.MapGet("/data", () => Results.Ok("ok"));
        await app.StartAsync(TestContext.Current.CancellationToken);

        var request = new HttpRequestMessage(HttpMethod.Options, "/data");
        request.Headers.Add("Origin", "https://allowed.example.com");
        request.Headers.Add("Access-Control-Request-Method", "GET");
        var response = await app.GetApiTestClient().SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        AssertSecurityHeadersPresent(response);
    }

    /// <summary>
    /// A rejection from anything the application places after the pipeline — an endpoint filter
    /// returning <c>Results.Problem</c>, an authorization handler, its own middleware — must still
    /// carry security headers. This is the general case the phase ordering exists to guarantee.
    /// </summary>
    [Fact]
    public async Task SecurityHeaders_Present_On_Downstream_Rejection()
    {
        await using var app = await TestAppBuilder.CreateAppAsync(Config(), Environments.Production);

        app.UseSimulatedHttps();
        app.UseApiPipeline(p => p.WithSecurityHeaders());
        app.Use((HttpContext context, RequestDelegate _) =>
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return Task.CompletedTask;
        });
        app.MapGet("/data", () => Results.Ok("ok"));
        await app.StartAsync(TestContext.Current.CancellationToken);

        var response = await app.GetApiTestClient().GetAsync("/data", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        AssertSecurityHeadersPresent(response);
    }

    /// <summary>
    /// Successful responses must keep their headers — the reordering must not regress the happy path.
    /// </summary>
    [Fact]
    public async Task SecurityHeaders_Still_Present_On_Successful_Response()
    {
        await using var app = await TestAppBuilder.CreateAppAsync(Config(), Environments.Production);

        app.UseSimulatedHttps();
        app.UseApiPipeline(p => p.WithSecurityHeaders());
        app.MapGet("/ok", () => Results.Ok("ok"));
        await app.StartAsync(TestContext.Current.CancellationToken);

        var response = await app.GetApiTestClient().GetAsync("/ok", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        AssertSecurityHeadersPresent(response);
    }
}
