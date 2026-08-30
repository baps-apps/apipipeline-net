using System.Net;
using ApiPipeline.NET.Correlation;
using ApiPipeline.NET.Pipeline;
using FluentAssertions;
using Microsoft.AspNetCore.TestHost;

namespace ApiPipeline.NET.Tests;

/// <summary>
/// Tests for <see cref="CorrelationIdPropagationHandler"/> and
/// <c>AddApiPipelineCorrelationIdPropagation()</c>, covering forwarding of the incoming request's correlation
/// ID onto outbound calls, and the cases where it must not overwrite an existing value.
/// </summary>
public sealed class CorrelationIdPropagationTests
{
    /// <summary>
    /// Captures the outbound request headers instead of performing real network I/O.
    /// </summary>
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    private static async Task<(WebApplication App, CapturingHandler Captured)> CreateAppAsync()
    {
        var captured = new CapturingHandler();
        var app = await TestAppBuilder.CreateAppAsync(
            TestAppBuilder.MinimalConfig(),
            configureServices: services => services
                .AddHttpClient("downstream", c => c.BaseAddress = new Uri("https://downstream.example.com"))
                .AddApiPipelineCorrelationIdPropagation()
                .ConfigurePrimaryHttpMessageHandler(() => captured));

        return (app, captured);
    }

    /// <summary>
    /// Verifies the incoming request's correlation ID is copied onto an outbound call, which is
    /// what makes the ID span the whole call chain rather than stopping at this service.
    /// </summary>
    [Fact]
    public async Task Forwards_CorrelationId_To_Downstream_Request()
    {
        var (app, captured) = await CreateAppAsync();
        await using var _ = app;

        app.UseApiPipeline(pipeline => pipeline.WithCorrelationId());
        app.MapGet("/test", async (IHttpClientFactory factory) =>
        {
            var client = factory.CreateClient("downstream");
            await client.GetAsync("/data");
            return Results.Ok("ok");
        });
        await app.StartAsync(TestContext.Current.CancellationToken);

        var request = new HttpRequestMessage(HttpMethod.Get, "/test");
        request.Headers.Add(CorrelationHeaders.CorrelationId, "chain-id-1");
        await app.GetTestClient().SendAsync(request, TestContext.Current.CancellationToken);

        captured.LastRequest.Should().NotBeNull();
        captured.LastRequest!.Headers.GetValues(CorrelationHeaders.CorrelationId).Single()
            .Should().Be("chain-id-1");
    }

    /// <summary>
    /// Verifies a generated correlation ID is forwarded too — propagation must not depend on the
    /// caller having supplied one.
    /// </summary>
    [Fact]
    public async Task Forwards_Generated_CorrelationId_When_Caller_Sent_None()
    {
        var (app, captured) = await CreateAppAsync();
        await using var _ = app;

        app.UseApiPipeline(pipeline => pipeline.WithCorrelationId());
        app.MapGet("/test", async (HttpContext ctx, IHttpClientFactory factory) =>
        {
            await factory.CreateClient("downstream").GetAsync("/data");
            return Results.Ok(new { id = ctx.GetCorrelationId() });
        });
        await app.StartAsync(TestContext.Current.CancellationToken);

        var response = await app.GetTestClient().GetAsync("/test", TestContext.Current.CancellationToken);
        var inboundId = response.Headers.GetValues(CorrelationHeaders.CorrelationId).Single();

        captured.LastRequest!.Headers.GetValues(CorrelationHeaders.CorrelationId).Single()
            .Should().Be(inboundId);
    }

    /// <summary>
    /// Verifies an explicitly set outbound correlation ID is left alone. A fan-out that assigns its
    /// own ID per branch must not have it silently replaced by the ambient request's ID.
    /// </summary>
    [Fact]
    public async Task Does_Not_Overwrite_Explicit_Outbound_CorrelationId()
    {
        var (app, captured) = await CreateAppAsync();
        await using var _ = app;

        app.UseApiPipeline(pipeline => pipeline.WithCorrelationId());
        app.MapGet("/test", async (IHttpClientFactory factory) =>
        {
            var outbound = new HttpRequestMessage(HttpMethod.Get, "/data");
            outbound.Headers.Add(CorrelationHeaders.CorrelationId, "explicit-branch-id");
            await factory.CreateClient("downstream").SendAsync(outbound);
            return Results.Ok("ok");
        });
        await app.StartAsync(TestContext.Current.CancellationToken);

        var request = new HttpRequestMessage(HttpMethod.Get, "/test");
        request.Headers.Add(CorrelationHeaders.CorrelationId, "ambient-id");
        await app.GetTestClient().SendAsync(request, TestContext.Current.CancellationToken);

        captured.LastRequest!.Headers.GetValues(CorrelationHeaders.CorrelationId).Single()
            .Should().Be("explicit-branch-id");
    }

    /// <summary>
    /// Verifies an outbound call made outside any HTTP request (background work, hosted service)
    /// simply sends no correlation header rather than throwing on the null <see cref="HttpContext"/>.
    /// </summary>
    [Fact]
    public async Task Sends_No_Header_When_No_Ambient_HttpContext()
    {
        var (app, captured) = await CreateAppAsync();
        await using var _ = app;

        await app.StartAsync(TestContext.Current.CancellationToken);

        var factory = app.Services.GetRequiredService<IHttpClientFactory>();
        await factory.CreateClient("downstream").GetAsync("/data", TestContext.Current.CancellationToken);

        captured.LastRequest!.Headers.Contains(CorrelationHeaders.CorrelationId).Should().BeFalse();
    }
}
