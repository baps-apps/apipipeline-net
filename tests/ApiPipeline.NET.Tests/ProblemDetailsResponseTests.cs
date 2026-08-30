using System.Net;
using System.Text.Json;
using ApiPipeline.NET.Pipeline;
using FluentAssertions;
using Microsoft.AspNetCore.TestHost;

namespace ApiPipeline.NET.Tests;

/// <summary>
/// The responses the pipeline produces itself (429 from either rate limiter, the request timeout's
/// status) carry an RFC 9457 <c>application/problem+json</c> body, without the application having to
/// add <c>UseStatusCodePages</c>. An application that registered <c>AddProblemDetails</c> gets its own
/// customization applied to them, as to every other error.
/// </summary>
public sealed class ProblemDetailsResponseTests
{
    private static async Task<JsonElement> ProblemAsync(HttpResponseMessage response)
    {
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        return JsonDocument.Parse(body).RootElement;
    }

    private static async Task<HttpResponseMessage> SecondRequestAsync(WebApplication app, Action<ApiPipelineBuilder> pipeline)
    {
        app.Use(async (ctx, next) =>
        {
            ctx.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.9");
            await next(ctx);
        });
        app.UseApiPipeline(pipeline);
        // Plain text, so the endpoint itself needs no JSON metadata.
        app.MapGet("/test", () => "ok");
        await app.StartAsync(TestContext.Current.CancellationToken);
        var client = app.GetTestClient();

        (await client.GetAsync("/test", TestContext.Current.CancellationToken)).StatusCode.Should().Be(HttpStatusCode.OK);
        return await client.GetAsync("/test", TestContext.Current.CancellationToken);
    }

    /// <summary>The main limiter's 429 is a problem document, with its retry time in the detail.</summary>
    [Fact]
    public async Task MainLimiterRejection_HasAProblemBody()
    {
        await using var app = await TestAppBuilder.CreateAppAsync(TestAppBuilder.WithRateLimiting(permitLimit: 1));

        var rejected = await SecondRequestAsync(app, p => p.WithRateLimiting());

        ((int)rejected.StatusCode).Should().Be(429);
        var problem = await ProblemAsync(rejected);
        problem.GetProperty("status").GetInt32().Should().Be(429);
        problem.GetProperty("title").GetString().Should().Be("Too Many Requests");
        problem.GetProperty("detail").GetString().Should().Contain(rejected.Headers.GetValues("Retry-After").Single());
    }

    /// <summary>The edge limiter's 429 has the same body: one rejection shape for both limiters.</summary>
    [Fact]
    public async Task EdgeLimiterRejection_HasAProblemBody()
    {
        await using var app = await TestAppBuilder.CreateAppAsync(TestAppBuilder.MinimalConfig(c =>
        {
            c["ApiPipeline:EdgeRateLimiting:Enabled"] = "true";
            c["ApiPipeline:EdgeRateLimiting:PermitLimit"] = "1";
        }));

        var rejected = await SecondRequestAsync(app, p => p.WithEdgeRateLimiting());

        ((int)rejected.StatusCode).Should().Be(429);
        var problem = await ProblemAsync(rejected);
        problem.GetProperty("status").GetInt32().Should().Be(429);
        problem.GetProperty("title").GetString().Should().Be("Too Many Requests");
    }

    /// <summary>
    /// An application's <c>AddProblemDetails</c> customization (here, the README's correlation id)
    /// applies to the pipeline's own 429, so every error has one shape.
    /// </summary>
    [Fact]
    public async Task Rejection_UsesTheApplicationsProblemDetailsCustomization()
    {
        await using var app = await TestAppBuilder.CreateAppAsync(TestAppBuilder.WithRateLimiting(permitLimit: 1), addProblemDetails: true);

        var rejected = await SecondRequestAsync(app, p => p.WithCorrelationId().WithRateLimiting());

        var problem = await ProblemAsync(rejected);
        problem.GetProperty("correlationId").GetString().Should().Be(rejected.Headers.GetValues("X-Correlation-Id").Single());
    }

    /// <summary>
    /// Without <c>AddProblemDetails</c> and with JSON options that know no <c>ProblemDetails</c> (as in
    /// a Native AOT app, where reflection-based serialization is off), the 429 is still a problem
    /// document, not a 500.
    /// </summary>
    [Fact]
    public async Task Rejection_WithoutReflectionJson_HasAProblemBody()
    {
        await using var app = await TestAppBuilder.CreateAppAsync(
            TestAppBuilder.WithRateLimiting(permitLimit: 1),
            configureServices: WithoutReflectionJson);
        var rejected = await SecondRequestAsync(app, p => p.WithRateLimiting());

        ((int)rejected.StatusCode).Should().Be(429);
        var problem = await ProblemAsync(rejected);
        problem.GetProperty("status").GetInt32().Should().Be(429);
        problem.GetProperty("title").GetString().Should().Be("Too Many Requests");
        problem.GetProperty("type").GetString().Should().Be(ApiPipeline.NET.RateLimiting.RateLimitRejection.ProblemType);
    }

    /// <summary>The timeout's body under the same JSON options: a problem document with the framework's defaults.</summary>
    [Fact]
    public async Task Timeout_WithoutReflectionJson_HasAProblemBody()
    {
        await using var app = await TestAppBuilder.CreateAppAsync(
            TestAppBuilder.MinimalConfig(c =>
            {
                c["ApiPipeline:RequestTimeouts:Enabled"] = "true";
                c["ApiPipeline:RequestTimeouts:DefaultTimeout"] = "00:00:01";
            }),
            configureServices: WithoutReflectionJson);
        app.UseApiPipeline(pipeline => pipeline.WithRequestTimeouts());
        app.MapGet("/slow", async (CancellationToken token) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), token);
            return Results.Ok("never");
        });
        await app.StartAsync(TestContext.Current.CancellationToken);

        var response = await app.GetTestClient().GetAsync("/slow", TestContext.Current.CancellationToken);

        ((int)response.StatusCode).Should().Be(504);
        var problem = await ProblemAsync(response);
        problem.GetProperty("status").GetInt32().Should().Be(504);
        problem.GetProperty("title").GetString().Should().NotBeNullOrEmpty();
        problem.GetProperty("type").GetString().Should().NotBeNullOrEmpty();
    }

    /// <summary>HTTP JSON options that resolve no type, as when reflection-based serialization is off.</summary>
    private static void WithoutReflectionJson(IServiceCollection services) =>
        services.ConfigureHttpJsonOptions(options =>
            options.SerializerOptions.TypeInfoResolver = System.Text.Json.Serialization.Metadata.JsonTypeInfoResolver.Combine());

    /// <summary>The request timeout's response is a problem document with the configured status.</summary>
    [Theory]
    [InlineData(null, 504)]
    [InlineData(503, 503)]
    public async Task Timeout_HasAProblemBody(int? configured, int expected)
    {
        await using var app = await TestAppBuilder.CreateAppAsync(TestAppBuilder.MinimalConfig(c =>
        {
            c["ApiPipeline:RequestTimeouts:Enabled"] = "true";
            c["ApiPipeline:RequestTimeouts:DefaultTimeout"] = "00:00:01";
            if (configured is { } code)
            {
                c["ApiPipeline:RequestTimeouts:TimeoutStatusCode"] = code.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
        }));
        app.UseApiPipeline(pipeline => pipeline.WithRequestTimeouts());
        app.MapGet("/slow", async (CancellationToken token) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), token);
            return Results.Ok("never");
        });
        await app.StartAsync(TestContext.Current.CancellationToken);

        var response = await app.GetTestClient().GetAsync("/slow", TestContext.Current.CancellationToken);

        ((int)response.StatusCode).Should().Be(expected);
        (await ProblemAsync(response)).GetProperty("status").GetInt32().Should().Be(expected);
    }
}
