using System.Net;
using ApiPipeline.NET.Configuration;
using ApiPipeline.NET.Correlation;
using ApiPipeline.NET.Pipeline;
using ApiPipeline.NET.Middleware;
using FluentAssertions;
using Microsoft.AspNetCore.TestHost;

namespace ApiPipeline.NET.Tests;

/// <summary>
/// Tests for <see cref="CorrelationIdMiddleware"/> that verify header generation,
/// propagation, validation, and availability within the HTTP context.
/// </summary>
public sealed class CorrelationIdMiddlewareTests
{
    /// <summary>
    /// Verifies that a new correlation ID is automatically generated and returned
    /// in the response when no <c>X-Correlation-Id</c> header is present in the request.
    /// </summary>
    [Fact]
    public async Task Generates_CorrelationId_When_Header_Missing()
    {
        await using var app = await TestAppBuilder.CreateAppAsync(TestAppBuilder.MinimalConfig());
        app.UseApiPipeline(pipeline => pipeline.WithCorrelationId());
        app.MapGet("/test", () => Results.Ok("ok"));
        await app.StartAsync(TestContext.Current.CancellationToken);

        var client = app.GetTestClient();
        var response = await client.GetAsync("/test", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.Contains(CorrelationIdMiddleware.HeaderName).Should().BeTrue();

        var correlationId = response.Headers.GetValues(CorrelationIdMiddleware.HeaderName).Single();
        correlationId.Should().NotBeNullOrWhiteSpace();
    }

    /// <summary>
    /// Verifies that a valid correlation ID supplied by the caller in the request header
    /// is echoed back unchanged in the response.
    /// </summary>
    [Fact]
    public async Task Propagates_Valid_CorrelationId_From_Request()
    {
        await using var app = await TestAppBuilder.CreateAppAsync(TestAppBuilder.MinimalConfig());
        app.UseApiPipeline(pipeline => pipeline.WithCorrelationId());
        app.MapGet("/test", () => Results.Ok("ok"));
        await app.StartAsync(TestContext.Current.CancellationToken);

        var client = app.GetTestClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "/test");
        request.Headers.Add("X-Correlation-Id", "valid-id-123");

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var correlationId = response.Headers.GetValues(CorrelationIdMiddleware.HeaderName).Single();
        correlationId.Should().Be("valid-id-123");
    }

    /// <summary>
    /// Verifies that malicious or malformed correlation IDs — including XSS payloads,
    /// CRLF injection attempts, empty strings, and whitespace-only values — are rejected
    /// and replaced with a freshly generated safe ID.
    /// </summary>
    /// <param name="invalidId">The invalid correlation ID supplied in the request header.</param>
    [Theory]
    [InlineData("id-with-<script>")] // XSS attempt
    [InlineData("id\r\nInjected-Header: evil")] // CRLF injection
    [InlineData("")] // empty
    [InlineData("   ")] // whitespace
    public async Task Rejects_Invalid_CorrelationId_And_Generates_New(string invalidId)
    {
        await using var app = await TestAppBuilder.CreateAppAsync(TestAppBuilder.MinimalConfig());
        app.UseApiPipeline(pipeline => pipeline.WithCorrelationId());
        app.MapGet("/test", () => Results.Ok("ok"));
        await app.StartAsync(TestContext.Current.CancellationToken);

        var client = app.GetTestClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "/test");
        request.Headers.TryAddWithoutValidation("X-Correlation-Id", invalidId);

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.Contains(CorrelationIdMiddleware.HeaderName).Should().BeTrue();

        var correlationId = response.Headers.GetValues(CorrelationIdMiddleware.HeaderName).Single();
        correlationId.Should().NotBe(invalidId);
        correlationId.Should().NotBeNullOrWhiteSpace();
    }

    /// <summary>
    /// Verifies that a correlation ID exceeding the maximum allowed length is rejected
    /// and replaced with a generated ID of at most 128 characters.
    /// </summary>
    [Fact]
    public async Task Rejects_Overly_Long_CorrelationId()
    {
        await using var app = await TestAppBuilder.CreateAppAsync(TestAppBuilder.MinimalConfig());
        app.UseApiPipeline(pipeline => pipeline.WithCorrelationId());
        app.MapGet("/test", () => Results.Ok("ok"));
        await app.StartAsync(TestContext.Current.CancellationToken);

        var longId = new string('a', 200);
        var client = app.GetTestClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "/test");
        request.Headers.TryAddWithoutValidation("X-Correlation-Id", longId);

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        var correlationId = response.Headers.GetValues(CorrelationIdMiddleware.HeaderName).Single();
        correlationId.Should().NotBe(longId);
        correlationId.Length.Should().BeLessOrEqualTo(128);
    }

    /// <summary>
    /// Verifies that correlation IDs using alphanumeric characters, hyphens, underscores,
    /// and dots are accepted and propagated without modification.
    /// </summary>
    /// <param name="validId">A valid correlation ID pattern to send in the request header.</param>
    [Theory]
    [InlineData("abc-123")]
    [InlineData("ABC_DEF.456")]
    [InlineData("trace-id-00112233445566778899aabbccddeeff")]
    public async Task Accepts_Valid_CorrelationId_Patterns(string validId)
    {
        await using var app = await TestAppBuilder.CreateAppAsync(TestAppBuilder.MinimalConfig());
        app.UseApiPipeline(pipeline => pipeline.WithCorrelationId());
        app.MapGet("/test", () => Results.Ok("ok"));
        await app.StartAsync(TestContext.Current.CancellationToken);

        var client = app.GetTestClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "/test");
        request.Headers.Add("X-Correlation-Id", validId);

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        var correlationId = response.Headers.GetValues(CorrelationIdMiddleware.HeaderName).Single();
        correlationId.Should().Be(validId);
    }

    /// <summary>
    /// Verifies that the resolved correlation ID is stored in <see cref="HttpContext.Items"/>
    /// and reachable through <c>GetCorrelationId()</c>, making it accessible to downstream
    /// middleware and handlers.
    /// </summary>
    [Fact]
    public async Task CorrelationId_Available_In_HttpContext_Items()
    {
        await using var app = await TestAppBuilder.CreateAppAsync(TestAppBuilder.MinimalConfig());
        app.UseApiPipeline(pipeline => pipeline.WithCorrelationId());
        app.MapGet("/test", (HttpContext ctx) =>
        {
            var id = ctx.GetCorrelationId();
            id.Should().Be("my-id-42");
            // Private object keys: a generic string key such as "CorrelationId" or "TraceId" can
            // collide with another library's entry in the same request.
            ctx.Items.Keys.OfType<string>().Should().NotContain(["CorrelationId", "TraceId"]);
            return Results.Ok(new { correlationId = id });
        });
        await app.StartAsync(TestContext.Current.CancellationToken);

        var client = app.GetTestClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "/test");
        request.Headers.Add("X-Correlation-Id", "my-id-42");

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        // The status first: a failed assertion inside the handler becomes a Development exception
        // page that echoes the request headers, which would satisfy the body check on its own.
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        body.Should().Contain("my-id-42");
    }

    /// <summary>
    /// Verifies that a caller-supplied correlation ID is echoed unchanged <em>and</em> the response
    /// still reports the trace ID separately, so the caller can pull the exact trace from the
    /// backend using an ID it did not choose itself.
    /// </summary>
    [Fact]
    public async Task Emits_TraceId_Header_Alongside_CallerSupplied_CorrelationId()
    {
        await using var app = await TestAppBuilder.CreateAppAsync(TestAppBuilder.MinimalConfig());
        app.UseApiPipeline(pipeline => pipeline.WithCorrelationId());
        app.MapGet("/test", () => Results.Ok("ok"));
        await app.StartAsync(TestContext.Current.CancellationToken);

        var client = app.GetTestClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "/test");
        request.Headers.Add(CorrelationHeaders.CorrelationId, "caller-chosen-id");

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.Headers.GetValues(CorrelationHeaders.CorrelationId).Single()
            .Should().Be("caller-chosen-id");
        response.Headers.Contains(CorrelationHeaders.TraceId).Should().BeTrue(
            "the caller cannot find the trace from an ID it supplied itself");
        response.Headers.GetValues(CorrelationHeaders.TraceId).Single()
            .Should().NotBeNullOrWhiteSpace();
    }

    /// <summary>
    /// Verifies that a generated correlation ID is not a copy of the trace ID. Reusing the trace ID
    /// would leave an operator unable to distinguish a caller-supplied ID from a generated one.
    /// </summary>
    [Fact]
    public async Task Generated_CorrelationId_Is_Distinct_From_TraceId()
    {
        await using var app = await TestAppBuilder.CreateAppAsync(TestAppBuilder.MinimalConfig());
        app.UseApiPipeline(pipeline => pipeline.WithCorrelationId());
        app.MapGet("/test", () => Results.Ok("ok"));
        await app.StartAsync(TestContext.Current.CancellationToken);

        var client = app.GetTestClient();
        var response = await client.GetAsync("/test", TestContext.Current.CancellationToken);

        var correlationId = response.Headers.GetValues(CorrelationHeaders.CorrelationId).Single();
        var traceId = response.Headers.GetValues(CorrelationHeaders.TraceId).Single();

        correlationId.Should().NotBe(traceId);
    }

    /// <summary>
    /// Verifies that <c>EmitTraceIdHeader = false</c> suppresses the response header while leaving
    /// the trace ID on <see cref="HttpContext.Items"/> for internal use.
    /// </summary>
    [Fact]
    public async Task EmitTraceIdHeader_False_Suppresses_Header_But_Keeps_Items_Value()
    {
        var config = TestAppBuilder.MinimalConfig(c => c["ApiPipeline:CorrelationId:EmitTraceIdHeader"] = "false");
        await using var app = await TestAppBuilder.CreateAppAsync(config);
        app.UseApiPipeline(pipeline => pipeline.WithCorrelationId());
        app.MapGet("/test", (HttpContext ctx) => Results.Ok(new { traceId = ctx.GetTraceId() }));
        await app.StartAsync(TestContext.Current.CancellationToken);

        var client = app.GetTestClient();
        var response = await client.GetAsync("/test", TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        response.Headers.Contains(CorrelationHeaders.TraceId).Should().BeFalse();
        response.Headers.Contains(CorrelationHeaders.CorrelationId).Should().BeTrue();
        body.Should().NotContain("\"traceId\":null");
    }

    /// <summary>
    /// Without a request <see cref="System.Diagnostics.Activity"/> there is no W3C trace id: no
    /// <c>X-Trace-Id</c>, and <c>GetTraceId()</c> is null. <c>TraceIdentifier</c> is not a trace id.
    /// </summary>
    [Fact]
    public async Task NoActivity_NoTraceIdHeader_AndGetTraceIdIsNull()
    {
        await using var app = await TestAppBuilder.CreateAppAsync(TestAppBuilder.MinimalConfig());
        app.Use((context, next) =>
        {
            // As if no listener, and no logging, had made ASP.NET Core create one.
            System.Diagnostics.Activity.Current = null;
            return next(context);
        });
        app.UseApiPipeline(pipeline => pipeline.WithCorrelationId());
        app.MapGet("/test", (HttpContext ctx) => Results.Ok(new { traceId = ctx.GetTraceId() }));
        await app.StartAsync(TestContext.Current.CancellationToken);

        var response = await app.GetTestClient().GetAsync("/test", TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        response.Headers.Contains(CorrelationHeaders.TraceId).Should().BeFalse();
        response.Headers.Contains(CorrelationHeaders.CorrelationId).Should().BeTrue();
        body.Should().Contain("\"traceId\":null");
    }

    /// <summary>
    /// Verifies that <c>AcceptInboundCorrelationId = false</c> ignores a well-formed caller ID,
    /// so an internet-facing API never lets a caller choose the value that lands in its logs.
    /// </summary>
    [Fact]
    public async Task AcceptInboundCorrelationId_False_Ignores_Caller_Supplied_Id()
    {
        var config = TestAppBuilder.MinimalConfig(c => c["ApiPipeline:CorrelationId:AcceptInboundCorrelationId"] = "false");
        await using var app = await TestAppBuilder.CreateAppAsync(config);
        app.UseApiPipeline(pipeline => pipeline.WithCorrelationId());
        app.MapGet("/test", () => Results.Ok("ok"));
        await app.StartAsync(TestContext.Current.CancellationToken);

        var client = app.GetTestClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "/test");
        request.Headers.Add(CorrelationHeaders.CorrelationId, "attacker-chosen");

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.Headers.GetValues(CorrelationHeaders.CorrelationId).Single()
            .Should().NotBe("attacker-chosen");
    }

    /// <summary>
    /// Verifies that a correlation ID sent twice is rejected rather than adopted as the
    /// comma-joined concatenation of both values.
    /// </summary>
    [Fact]
    public async Task Rejects_Repeated_CorrelationId_Header()
    {
        await using var app = await TestAppBuilder.CreateAppAsync(TestAppBuilder.MinimalConfig());
        app.UseApiPipeline(pipeline => pipeline.WithCorrelationId());
        app.MapGet("/test", () => Results.Ok("ok"));
        await app.StartAsync(TestContext.Current.CancellationToken);

        var client = app.GetTestClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "/test");
        request.Headers.TryAddWithoutValidation(CorrelationHeaders.CorrelationId, "first-id");
        request.Headers.TryAddWithoutValidation(CorrelationHeaders.CorrelationId, "second-id");

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        var correlationId = response.Headers.GetValues(CorrelationHeaders.CorrelationId).Single();
        correlationId.Should().NotContain("first-id");
        correlationId.Should().NotContain("second-id");
    }

    /// <summary>
    /// Both tracing headers survive an exception handler that calls <c>Response.Clear()</c>.
    /// </summary>
    [Fact]
    public async Task CorrelationId_Survives_Exception_Response()
    {
        await using var app = await TestAppBuilder.CreateAppAsync(TestAppBuilder.MinimalConfig());
        app.UseApiPipeline(pipeline => pipeline.WithCorrelationId());
        app.UseExceptionHandler(errorApp => errorApp.Run(ctx =>
        {
            ctx.Response.Clear();
            ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
            return Task.CompletedTask;
        }));
        app.MapGet("/boom", void () => throw new InvalidOperationException("boom"));
        await app.StartAsync(TestContext.Current.CancellationToken);

        var client = app.GetTestClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "/boom");
        request.Headers.Add(CorrelationHeaders.CorrelationId, "err-trace-1");

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        response.Headers.GetValues(CorrelationHeaders.CorrelationId).Single().Should().Be("err-trace-1");
        response.Headers.Contains(CorrelationHeaders.TraceId).Should().BeTrue();
    }

    /// <summary>
    /// The middleware is convention-based, not a container registration: <c>IMiddleware</c> would
    /// cost a factory call, a transient resolution and a release per request.
    /// </summary>
    [Fact]
    public async Task CorrelationIdMiddleware_IsNotRegisteredInDi()
    {
        await using var app = await TestAppBuilder.CreateAppAsync(TestAppBuilder.MinimalConfig());

        app.Services.GetService<CorrelationIdMiddleware>().Should().BeNull(
            "the middleware is convention-based: UseMiddleware constructs it once, from the " +
            "container's singletons, with no service registration of its own");
    }

    /// <summary>
    /// What the registration call must actually deliver: validation of the options the middleware
    /// reads, so an out-of-range value fails startup rather than reaching a request.
    /// </summary>
    [Fact]
    public async Task AddCorrelationId_Registers_ValidationForTheOptionsTheMiddlewareReads()
    {
        await using var app = await TestAppBuilder.CreateAppAsync(TestAppBuilder.MinimalConfig());

        app.Services.GetServices<Microsoft.Extensions.Options.IValidateOptions<ApiPipelineCorrelationIdOptions>>()
            .Should().NotBeEmpty("AddApiPipelineCorrelationId must register the options validator");
    }
}
