using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Time.Testing;

namespace ApiPipeline.NET.Tests;

/// <summary>
/// The edge limiter's 429 says when the caller's window resets, not the full window length, so
/// well-behaved clients are not held back longer than needed.
/// </summary>
public sealed class EdgeRetryAfterTests
{
    private static async Task<(WebApplication App, HttpClient Client, FakeTimeProvider Time)> StartAsync(bool emitQuota = false)
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-11-12T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        var config = TestAppBuilder.MinimalConfig(c =>
        {
            c["ApiPipeline:EdgeRateLimiting:Enabled"] = "true";
            c["ApiPipeline:EdgeRateLimiting:PermitLimit"] = "1";
            c["ApiPipeline:EdgeRateLimiting:Window"] = "00:01:00";
            c["ApiPipeline:RateLimiting:EmitRateLimitHeadersOnRejection"] = emitQuota ? "true" : "false";
        });
        var app = await TestAppBuilder.CreateAppAsync(
            config,
            configureServices: services => services.AddSingleton<TimeProvider>(time));
        app.Use((context, next) =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Parse("198.51.100.7");
            return next(context);
        });
        app.UseApiPipeline(pipeline => pipeline.WithEdgeRateLimiting());
        app.MapGet("/test", () => Results.Ok("ok"));
        await app.StartAsync(TestContext.Current.CancellationToken);
        return (app, app.GetTestClient(), time);
    }

    [Fact]
    public async Task Rejection_RetryAfter_IsWhatIsLeftOfTheWindow()
    {
        var (app, client, time) = await StartAsync();
        await using var _ = app;

        (await client.GetAsync("/test", TestContext.Current.CancellationToken)).StatusCode.Should().Be(HttpStatusCode.OK);
        time.Advance(TimeSpan.FromSeconds(20));
        var rejected = await client.GetAsync("/test", TestContext.Current.CancellationToken);

        ((int)rejected.StatusCode).Should().Be(429);
        rejected.Headers.RetryAfter!.Delta.Should().Be(TimeSpan.FromSeconds(40));
    }

    /// <summary>A fraction of a second left still says one second: Retry-After counts whole seconds.</summary>
    [Fact]
    public async Task Rejection_RetryAfter_RoundsUp_ToAtLeastOneSecond()
    {
        var (app, client, time) = await StartAsync();
        await using var _ = app;

        await client.GetAsync("/test", TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromSeconds(59.7));
        var rejected = await client.GetAsync("/test", TestContext.Current.CancellationToken);

        ((int)rejected.StatusCode).Should().Be(429);
        rejected.Headers.RetryAfter!.Delta.Should().Be(TimeSpan.FromSeconds(1));
    }

    /// <summary>The caller who waits as told is let in.</summary>
    [Fact]
    public async Task CallerWhoWaitsTheRetryAfter_IsAdmitted()
    {
        var (app, client, time) = await StartAsync();
        await using var _ = app;

        await client.GetAsync("/test", TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromSeconds(20));
        var rejected = await client.GetAsync("/test", TestContext.Current.CancellationToken);
        time.Advance(rejected.Headers.RetryAfter!.Delta!.Value);

        (await client.GetAsync("/test", TestContext.Current.CancellationToken)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>The IETF <c>RateLimit</c> field's reset (<c>t</c>) is the same remaining time.</summary>
    [Fact]
    public async Task Rejection_RateLimitField_ResetIsWhatIsLeftOfTheWindow()
    {
        var (app, client, time) = await StartAsync(emitQuota: true);
        await using var _ = app;

        await client.GetAsync("/test", TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromSeconds(45));
        var rejected = await client.GetAsync("/test", TestContext.Current.CancellationToken);

        rejected.Headers.GetValues("RateLimit").Single().Should().Be("\"edge\";r=0;t=15");
        rejected.Headers.GetValues("RateLimit-Policy").Single().Should().Be("\"edge\";q=1;w=60");
    }
}
