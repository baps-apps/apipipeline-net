using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Options;

namespace ApiPipeline.NET.Tests;

/// <summary>
/// Tests for the request timeout phase, which delegates to ASP.NET Core's
/// <c>RequestTimeouts</c> middleware (.NET 8+).
/// The ceiling applies when enabled, is inert otherwise, and per-endpoint opt-out still works.
/// </summary>
public sealed class RequestTimeoutTests
{
    private static Dictionary<string, string?> TimeoutConfig(int seconds = 1, int? statusCode = null)
        => TestAppBuilder.MinimalConfig(c =>
        {
            c["ApiPipeline:RequestTimeouts:Enabled"] = "true";
            c["ApiPipeline:RequestTimeouts:DefaultTimeout"] = TimeSpan.FromSeconds(seconds).ToString("c", System.Globalization.CultureInfo.InvariantCulture);
            if (statusCode is { } code)
            {
                c["ApiPipeline:RequestTimeouts:TimeoutStatusCode"] = code.ToString();
            }
        });

    /// <summary>
    /// A request that outlives the configured ceiling is cut off with 504 rather than held open.
    /// </summary>
    [Fact]
    public async Task SlowRequest_IsTerminatedWithGatewayTimeout()
    {
        await using var app = await TestAppBuilder.CreateAppAsync(TimeoutConfig(seconds: 1));
        app.UseApiPipeline(pipeline => pipeline.WithRequestTimeouts());
        app.MapGet("/slow", async (CancellationToken token) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), token);
            return Results.Ok("never");
        });
        await app.StartAsync(TestContext.Current.CancellationToken);

        var response = await app.GetTestClient().GetAsync("/slow", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.GatewayTimeout);
    }

    /// <summary>
    /// The ceiling must not interfere with requests that finish inside it.
    /// </summary>
    [Fact]
    public async Task FastRequest_IsUnaffected()
    {
        await using var app = await TestAppBuilder.CreateAppAsync(TimeoutConfig(seconds: 30));
        app.UseApiPipeline(pipeline => pipeline.WithRequestTimeouts());
        app.MapGet("/fast", () => Results.Ok("ok"));
        await app.StartAsync(TestContext.Current.CancellationToken);

        var response = await app.GetTestClient().GetAsync("/fast", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>
    /// Disabled is inert: a slow request completes normally, so turning the feature on is always an
    /// explicit operator decision.
    /// </summary>
    [Fact]
    public async Task Disabled_DoesNotTimeOutRequests()
    {
        var config = TestAppBuilder.MinimalConfig(c =>
        {
            c["ApiPipeline:RequestTimeouts:Enabled"] = "false";
            c["ApiPipeline:RequestTimeouts:DefaultTimeout"] = "00:00:01";
        });
        await using var app = await TestAppBuilder.CreateAppAsync(config);
        app.UseApiPipeline(pipeline => pipeline.WithRequestTimeouts());
        // Honours the request token: the timeout middleware only cancels it, so an endpoint that
        // ignored it would return 200 even with timeouts on, and the test could not fail.
        app.MapGet("/slow", async (CancellationToken token) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(2), token);
            return Results.Ok("done");
        });
        await app.StartAsync(TestContext.Current.CancellationToken);

        var response = await app.GetTestClient().GetAsync("/slow", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>
    /// A streaming or long-poll endpoint opts out with the framework's own
    /// <c>DisableRequestTimeout</c>. Pinned because a custom exclusion list here would be a
    /// reimplementation of that attribute.
    /// </summary>
    [Fact]
    public async Task EndpointWithDisableRequestTimeout_IsExempt()
    {
        await using var app = await TestAppBuilder.CreateAppAsync(TimeoutConfig(seconds: 1));
        app.UseApiPipeline(pipeline => pipeline.WithRequestTimeouts());
        // Honours the request token, so only the exemption keeps this request from a 504.
        app.MapGet("/stream", async (CancellationToken token) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(2), token);
            return Results.Ok("streamed");
        }).DisableRequestTimeout();
        await app.StartAsync(TestContext.Current.CancellationToken);

        var response = await app.GetTestClient().GetAsync("/stream", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>
    /// The status code is configurable: behind an ingress that treats 504 specially, an operator
    /// may prefer 503.
    /// </summary>
    [Fact]
    public async Task TimeoutStatusCode_IsHonoured()
    {
        await using var app = await TestAppBuilder.CreateAppAsync(TimeoutConfig(seconds: 1, statusCode: 503));
        app.UseApiPipeline(pipeline => pipeline.WithRequestTimeouts());
        app.MapGet("/slow", async (CancellationToken token) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), token);
            return Results.Ok("never");
        });
        await app.StartAsync(TestContext.Current.CancellationToken);

        var response = await app.GetTestClient().GetAsync("/slow", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }

    /// <summary>
    /// A timed-out response is still a response a caller has to be able to report: the correlation
    /// id must be on it. This pins the phase order — timeouts run inside the correlation and
    /// security-header phases, not outside them.
    /// </summary>
    [Fact]
    public async Task TimedOutResponse_CarriesCorrelationId()
    {
        await using var app = await TestAppBuilder.CreateAppAsync(TimeoutConfig(seconds: 1));
        app.UseApiPipeline(pipeline => pipeline
            .WithCorrelationId()
            .WithRequestTimeouts());
        app.MapGet("/slow", async (CancellationToken token) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), token);
            return Results.Ok("never");
        });
        await app.StartAsync(TestContext.Current.CancellationToken);

        var response = await app.GetTestClient().GetAsync("/slow", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.GatewayTimeout);
        response.Headers.Contains("X-Correlation-Id").Should().BeTrue();
    }

    /// <summary>
    /// Enabled with a nonsense ceiling must fail at startup, not clamp silently.
    /// </summary>
    [Fact]
    public async Task ZeroTimeout_FailsStartup()
    {
        var config = TestAppBuilder.MinimalConfig(c =>
        {
            c["ApiPipeline:RequestTimeouts:Enabled"] = "true";
            c["ApiPipeline:RequestTimeouts:DefaultTimeout"] = "00:00:00";
        });
        await using var app = await TestAppBuilder.CreateAppAsync(config);
        app.MapGet("/test", () => "ok");

        var act = () => app.StartAsync();

        await act.Should().ThrowAsync<OptionsValidationException>()
            .WithMessage("*DefaultTimeout*");
    }
}
