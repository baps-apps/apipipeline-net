using System.Net;
using System.Text.Json;
using ApiPipeline.NET.Extensions;
using ApiPipeline.NET.Validation;
using FluentAssertions;
using Microsoft.AspNetCore.TestHost;

namespace ApiPipeline.NET.Tests;

/// <summary>
/// Tests for the IRequestValidationFilter pipeline hook.
/// </summary>
public sealed class RequestValidationMiddlewareTests
{
    /// <summary>
    /// Verifies that a passing filter allows the request through.
    /// </summary>
    [Fact]
    public async Task ValidFilter_Allows_Request()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(TestAppBuilder.MinimalConfig());
        builder.Services.AddRouting();
        builder.Services.AddApiPipelineExceptionHandler();
        builder.Services.AddRequestValidation<AlwaysValidFilter>();
        await using var app = builder.Build();

        app.UseApiPipelineExceptionHandler();
        app.UseRequestValidation();
        app.MapGet("/test", () => Results.Ok("ok"));
        await app.StartAsync();

        var response = await app.GetTestClient().GetAsync("/test");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>
    /// Verifies that a rejecting filter returns 400 with ProblemDetails.
    /// </summary>
    [Fact]
    public async Task RejectingFilter_Returns_ProblemDetails_400()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(TestAppBuilder.MinimalConfig());
        builder.Services.AddRouting();
        builder.Services.AddApiPipelineExceptionHandler();
        builder.Services.AddRequestValidation<AlwaysRejectFilter>();
        await using var app = builder.Build();

        app.UseApiPipelineExceptionHandler();
        app.UseRequestValidation();
        app.MapGet("/test", () => Results.Ok("ok"));
        await app.StartAsync();

        var response = await app.GetTestClient().GetAsync("/test");
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");

        var body = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(body);
        json.RootElement.GetProperty("status").GetInt32().Should().Be(400);
    }

    private sealed class AlwaysValidFilter : IRequestValidationFilter
    {
        public ValueTask<RequestValidationResult> ValidateAsync(HttpContext context)
            => ValueTask.FromResult(RequestValidationResult.Valid);
    }

    private sealed class AlwaysRejectFilter : IRequestValidationFilter
    {
        public ValueTask<RequestValidationResult> ValidateAsync(HttpContext context)
            => ValueTask.FromResult(RequestValidationResult.Invalid(400, "Validation failed by test filter."));
    }
}
