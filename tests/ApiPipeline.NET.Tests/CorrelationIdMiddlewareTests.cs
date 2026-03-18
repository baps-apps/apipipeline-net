using System.Net;
using System.Net.Http.Headers;
using ApiPipeline.NET.Extensions;
using ApiPipeline.NET.Middleware;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;

namespace ApiPipeline.NET.Tests;

public sealed class CorrelationIdMiddlewareTests
{
    [Fact]
    public async Task Generates_CorrelationId_When_Header_Missing()
    {
        await using var app = await TestAppBuilder.CreateAppAsync(TestAppBuilder.MinimalConfig());
        app.UseCorrelationId();
        app.MapGet("/test", () => Results.Ok("ok"));
        await app.StartAsync();

        var client = app.GetTestClient();
        var response = await client.GetAsync("/test");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.Contains(CorrelationIdMiddleware.HeaderName).Should().BeTrue();

        var correlationId = response.Headers.GetValues(CorrelationIdMiddleware.HeaderName).Single();
        correlationId.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Propagates_Valid_CorrelationId_From_Request()
    {
        await using var app = await TestAppBuilder.CreateAppAsync(TestAppBuilder.MinimalConfig());
        app.UseCorrelationId();
        app.MapGet("/test", () => Results.Ok("ok"));
        await app.StartAsync();

        var client = app.GetTestClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "/test");
        request.Headers.Add("X-Correlation-Id", "valid-id-123");

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var correlationId = response.Headers.GetValues(CorrelationIdMiddleware.HeaderName).Single();
        correlationId.Should().Be("valid-id-123");
    }

    [Theory]
    [InlineData("id-with-<script>")] // XSS attempt
    [InlineData("id\r\nInjected-Header: evil")] // CRLF injection
    [InlineData("")] // empty
    [InlineData("   ")] // whitespace
    public async Task Rejects_Invalid_CorrelationId_And_Generates_New(string invalidId)
    {
        await using var app = await TestAppBuilder.CreateAppAsync(TestAppBuilder.MinimalConfig());
        app.UseCorrelationId();
        app.MapGet("/test", () => Results.Ok("ok"));
        await app.StartAsync();

        var client = app.GetTestClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "/test");
        request.Headers.TryAddWithoutValidation("X-Correlation-Id", invalidId);

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.Contains(CorrelationIdMiddleware.HeaderName).Should().BeTrue();

        var correlationId = response.Headers.GetValues(CorrelationIdMiddleware.HeaderName).Single();
        correlationId.Should().NotBe(invalidId);
        correlationId.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Rejects_Overly_Long_CorrelationId()
    {
        await using var app = await TestAppBuilder.CreateAppAsync(TestAppBuilder.MinimalConfig());
        app.UseCorrelationId();
        app.MapGet("/test", () => Results.Ok("ok"));
        await app.StartAsync();

        var longId = new string('a', 200);
        var client = app.GetTestClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "/test");
        request.Headers.TryAddWithoutValidation("X-Correlation-Id", longId);

        var response = await client.SendAsync(request);

        var correlationId = response.Headers.GetValues(CorrelationIdMiddleware.HeaderName).Single();
        correlationId.Should().NotBe(longId);
        correlationId.Length.Should().BeLessOrEqualTo(128);
    }

    [Theory]
    [InlineData("abc-123")]
    [InlineData("ABC_DEF.456")]
    [InlineData("trace-id-00112233445566778899aabbccddeeff")]
    public async Task Accepts_Valid_CorrelationId_Patterns(string validId)
    {
        await using var app = await TestAppBuilder.CreateAppAsync(TestAppBuilder.MinimalConfig());
        app.UseCorrelationId();
        app.MapGet("/test", () => Results.Ok("ok"));
        await app.StartAsync();

        var client = app.GetTestClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "/test");
        request.Headers.Add("X-Correlation-Id", validId);

        var response = await client.SendAsync(request);

        var correlationId = response.Headers.GetValues(CorrelationIdMiddleware.HeaderName).Single();
        correlationId.Should().Be(validId);
    }

    [Fact]
    public async Task CorrelationId_Available_In_HttpContext_Items()
    {
        await using var app = await TestAppBuilder.CreateAppAsync(TestAppBuilder.MinimalConfig());
        app.UseCorrelationId();
        app.MapGet("/test", (HttpContext ctx) =>
        {
            var id = ctx.Items[CorrelationIdMiddleware.HeaderName]?.ToString();
            return Results.Ok(new { correlationId = id });
        });
        await app.StartAsync();

        var client = app.GetTestClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "/test");
        request.Headers.Add("X-Correlation-Id", "my-id-42");

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        body.Should().Contain("my-id-42");
    }
}
