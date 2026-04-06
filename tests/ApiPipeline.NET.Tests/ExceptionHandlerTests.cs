using System.Net;
using System.Text.Json;
using ApiPipeline.NET.Extensions;
using ApiPipeline.NET.Middleware;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;

namespace ApiPipeline.NET.Tests;

/// <summary>
/// Tests for the ApiPipeline.NET global exception handler, verifying that unhandled
/// exceptions are converted to RFC 7807 <c>application/problem+json</c> responses
/// and that normal requests pass through unaffected.
/// </summary>
public sealed class ExceptionHandlerTests
{
    /// <summary>
    /// Verifies that an unhandled exception thrown by an endpoint produces an HTTP 500
    /// response with <c>application/problem+json</c> content, a <c>Cache-Control: no-store</c>
    /// header, and a JSON body containing <c>status</c>, <c>correlationId</c>, and <c>traceId</c>
    /// fields.
    /// </summary>
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

    /// <summary>
    /// Verifies that the <c>correlationId</c> field in the problem-details response body
    /// matches the <c>X-Correlation-Id</c> value supplied by the caller in the request.
    /// </summary>
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

    /// <summary>
    /// Verifies that a request to a non-existent route returns HTTP 404
    /// without the exception handler interfering with the status code response.
    /// </summary>
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

    /// <summary>
    /// Verifies that successful requests are not affected by the presence of the exception
    /// handler middleware — the response body and status code remain unchanged.
    /// </summary>
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

    /// <summary>
    /// Verifies that calling UseApiPipelineExceptionHandler without first calling
    /// AddApiPipelineExceptionHandler throws a clear InvalidOperationException at pipeline
    /// build time rather than silently falling back to plain-text error responses.
    /// </summary>
    [Fact]
    public async Task UseApiPipelineExceptionHandler_Without_AddService_Throws()
    {
        var config = TestAppBuilder.MinimalConfig();
        // addExceptionHandler: false — skips AddApiPipelineExceptionHandler
        await using var app = await TestAppBuilder.CreateAppAsync(config, addExceptionHandler: false);

        var act = () =>
        {
            app.UseApiPipelineExceptionHandler();
            return Task.CompletedTask;
        };

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*AddApiPipelineExceptionHandler*");
    }
}
