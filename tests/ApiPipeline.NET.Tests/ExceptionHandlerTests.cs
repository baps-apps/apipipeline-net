using System.Net;
using System.Text.Json;
using ApiPipeline.NET.Extensions;
using ApiPipeline.NET.Middleware;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;

namespace ApiPipeline.NET.Tests;

public sealed class ExceptionHandlerTests
{
    [Fact]
    public async Task Returns_ProblemDetails_On_Unhandled_Exception()
    {
        var config = TestAppBuilder.MinimalConfig();
        await using var app = await TestAppBuilder.CreateAppAsync(config, addExceptionHandler: true);

        app.UseCorrelationId();
        app.UseApiPipelineExceptionHandler();
        app.MapGet("/throw", IResult () => throw new InvalidOperationException("Test exception"));
        await app.StartAsync();

        var client = app.GetTestClient();
        var response = await client.GetAsync("/throw");

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
        response.Headers.CacheControl?.NoStore.Should().BeTrue();

        var body = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(body);

        json.RootElement.GetProperty("status").GetInt32().Should().Be(500);
        json.RootElement.TryGetProperty("correlationId", out _).Should().BeTrue();
        json.RootElement.TryGetProperty("traceId", out _).Should().BeTrue();
    }

    [Fact]
    public async Task ProblemDetails_Contains_CorrelationId_From_Request()
    {
        var config = TestAppBuilder.MinimalConfig();
        await using var app = await TestAppBuilder.CreateAppAsync(config, addExceptionHandler: true);

        app.UseCorrelationId();
        app.UseApiPipelineExceptionHandler();
        app.MapGet("/throw", IResult () => throw new Exception("boom"));
        await app.StartAsync();

        var client = app.GetTestClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "/throw");
        request.Headers.Add("X-Correlation-Id", "test-corr-id-42");

        var response = await client.SendAsync(request);

        var body = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(body);

        json.RootElement.GetProperty("correlationId").GetString().Should().Be("test-corr-id-42");
    }

    [Fact]
    public async Task Returns_StatusCodePage_For_404()
    {
        var config = TestAppBuilder.MinimalConfig();
        await using var app = await TestAppBuilder.CreateAppAsync(config, addExceptionHandler: true);

        app.UseCorrelationId();
        app.UseApiPipelineExceptionHandler();
        app.MapGet("/exists", () => Results.Ok("ok"));
        await app.StartAsync();

        var client = app.GetTestClient();
        var response = await client.GetAsync("/does-not-exist");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Normal_Requests_Not_Affected_By_ExceptionHandler()
    {
        var config = TestAppBuilder.MinimalConfig();
        await using var app = await TestAppBuilder.CreateAppAsync(config, addExceptionHandler: true);

        app.UseCorrelationId();
        app.UseApiPipelineExceptionHandler();
        app.MapGet("/ok", () => Results.Ok("success"));
        await app.StartAsync();

        var client = app.GetTestClient();
        var response = await client.GetAsync("/ok");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("success");
    }
}
