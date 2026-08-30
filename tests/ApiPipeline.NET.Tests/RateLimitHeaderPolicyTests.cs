using System.Net;
using ApiPipeline.NET.Pipeline;
using FluentAssertions;
using Microsoft.AspNetCore.TestHost;

namespace ApiPipeline.NET.Tests;

/// <summary>
/// When the IETF <c>RateLimit-Policy</c> / <c>RateLimit</c> fields are sent: on success only when opted in (it costs a
/// middleware frame), and on 429 according to <c>EmitRateLimitHeadersOnRejection</c>.
/// </summary>
public sealed class RateLimitHeaderPolicyTests
{
    private static Dictionary<string, string?> Config(
        bool? emitHeaders,
        int permitLimit,
        int windowSeconds,
        bool? emitOnRejection = null)
        => TestAppBuilder.WithRateLimiting(permitLimit: permitLimit, windowSeconds: windowSeconds)
            .Also(c =>
            {
                if (emitHeaders is { } emit)
                {
                    c["ApiPipeline:RateLimiting:EmitRateLimitHeaders"] = emit ? "true" : "false";
                }

                if (emitOnRejection is { } onRejection)
                {
                    c["ApiPipeline:RateLimiting:EmitRateLimitHeadersOnRejection"] =
                        onRejection ? "true" : "false";
                }
            });

    /// <summary>
    /// With the quota headers opted in, a throttled caller is told the limit and the window even
    /// though the informational (success-path) headers are off — that is the response the
    /// information exists for, and the two switches are independent.
    /// </summary>
    [Fact]
    public async Task Rejection_CarriesRateLimitHeaders_WhenInformationalHeadersAreOff()
    {
        await using var app = await TestAppBuilder.CreateAppAsync(
            Config(emitHeaders: false, permitLimit: 1, windowSeconds: 60, emitOnRejection: true),
            addProblemDetails: true);
        app.UseApiPipeline(pipeline => pipeline.WithRateLimiting());
        app.MapGet("/test", () => Results.Ok("ok"));
        await app.StartAsync(TestContext.Current.CancellationToken);
        var client = app.GetTestClient();

        await client.GetAsync("/test", TestContext.Current.CancellationToken);
        var rejected = await client.GetAsync("/test", TestContext.Current.CancellationToken);

        rejected.StatusCode.Should().Be((HttpStatusCode)429);
        RateLimitHeaders.PolicyValues(rejected).Should().Equal("\"strict\";q=1;w=60");
        RateLimitHeaders.Status(rejected).Should().Be("\"strict\";r=0;t=60",
            "a rejection reports no remaining quota and the seconds until a retry can succeed");
    }

    /// <summary>
    /// With the headers off, a successful response carries none of them — no per-request cost.
    /// </summary>
    [Fact]
    public async Task SuccessfulResponse_CarriesNoRateLimitHeaders_ByDefault()
    {
        await using var app = await TestAppBuilder.CreateAppAsync(
            Config(emitHeaders: null, permitLimit: 10, windowSeconds: 60), addProblemDetails: true);
        app.UseApiPipeline(pipeline => pipeline.WithRateLimiting());
        app.MapGet("/test", () => Results.Ok("ok"));
        await app.StartAsync(TestContext.Current.CancellationToken);

        var response = await app.GetTestClient().GetAsync("/test", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.Contains(RateLimitHeaders.PolicyHeader).Should().BeFalse(
            "the informational headers are opt-in; the 429 path always reports the limit");
    }

    /// <summary>
    /// Opting in still works for clients that implement adaptive backoff from steady-state
    /// responses.
    /// </summary>
    [Fact]
    public async Task SuccessfulResponse_CarriesRateLimitHeaders_WhenOptedIn()
    {
        await using var app = await TestAppBuilder.CreateAppAsync(
            Config(emitHeaders: true, permitLimit: 10, windowSeconds: 120), addProblemDetails: true);
        app.UseApiPipeline(pipeline => pipeline.WithRateLimiting());
        app.MapGet("/test", () => Results.Ok("ok"));
        await app.StartAsync(TestContext.Current.CancellationToken);

        var response = await app.GetTestClient().GetAsync("/test", TestContext.Current.CancellationToken);

        RateLimitHeaders.Quota(response).Should().Be(10);
        RateLimitHeaders.Window(response).Should().Be(120);
    }

    /// <summary>
    /// With the informational headers on, a 429 is written by two paths — the success-path
    /// middleware's <c>OnStarting</c> callback and the rejection callback. Both must produce one
    /// header value, not two: a duplicated <c>RateLimit-Policy</c> quota breaks strict client parsers.
    /// </summary>
    [Fact]
    public async Task Rejection_WithInformationalHeadersOn_EmitsEachHeaderOnce()
    {
        await using var app = await TestAppBuilder.CreateAppAsync(
            Config(emitHeaders: true, permitLimit: 1, windowSeconds: 60, emitOnRejection: true),
            addProblemDetails: true);
        app.UseApiPipeline(pipeline => pipeline.WithRateLimiting());
        app.MapGet("/test", () => Results.Ok("ok"));
        await app.StartAsync(TestContext.Current.CancellationToken);
        var client = app.GetTestClient();

        await client.GetAsync("/test", TestContext.Current.CancellationToken);
        var rejected = await client.GetAsync("/test", TestContext.Current.CancellationToken);

        rejected.StatusCode.Should().Be((HttpStatusCode)429);
        RateLimitHeaders.PolicyValues(rejected).Should().ContainSingle();
        RateLimitHeaders.Quota(rejected).Should().Be(1);
        RateLimitHeaders.Window(rejected).Should().Be(60);
    }

    /// <summary>
    /// The default is off. Pinned because flipping it back would silently re-add per-request work to
    /// every application in the organization.
    /// </summary>
    [Fact]
    public void EmitRateLimitHeaders_DefaultsToFalse()
    {
        new ApiPipelineRateLimitingOptions().EmitRateLimitHeaders.Should().BeFalse();
    }

    /// <summary>
    /// The quota headers on a 429 hand an unauthenticated caller the exact permit limit and window
    /// to calibrate a flood against, and the edge limiter rejects before authentication so there is
    /// no way to send them only to trusted callers. An internet-facing API must be able to withhold
    /// them.
    /// </summary>
    [Fact]
    public async Task Rejection_OmitsQuotaHeaders_WhenEmitOnRejectionIsOff()
    {
        await using var app = await TestAppBuilder.CreateAppAsync(
            Config(emitHeaders: false, permitLimit: 1, windowSeconds: 60, emitOnRejection: false),
            addProblemDetails: true);
        app.UseApiPipeline(pipeline => pipeline.WithRateLimiting());
        app.MapGet("/test", () => Results.Ok("ok"));
        await app.StartAsync(TestContext.Current.CancellationToken);
        var client = app.GetTestClient();

        await client.GetAsync("/test", TestContext.Current.CancellationToken);
        var rejected = await client.GetAsync("/test", TestContext.Current.CancellationToken);

        rejected.StatusCode.Should().Be((HttpStatusCode)429);
        rejected.Headers.Contains(RateLimitHeaders.PolicyHeader).Should().BeFalse();
        rejected.Headers.Contains(RateLimitHeaders.StatusHeader).Should().BeFalse();
    }

    /// <summary>
    /// Withholding the quota must not withhold <c>Retry-After</c>: it is what a well-behaved client
    /// backs off on, and it discloses the window rather than the quota.
    /// </summary>
    [Fact]
    public async Task Rejection_StillSendsRetryAfter_WhenQuotaHeadersAreWithheld()
    {
        await using var app = await TestAppBuilder.CreateAppAsync(
            Config(emitHeaders: false, permitLimit: 1, windowSeconds: 60, emitOnRejection: false),
            addProblemDetails: true);
        app.UseApiPipeline(pipeline => pipeline.WithRateLimiting());
        app.MapGet("/test", () => Results.Ok("ok"));
        await app.StartAsync(TestContext.Current.CancellationToken);
        var client = app.GetTestClient();

        await client.GetAsync("/test", TestContext.Current.CancellationToken);
        var rejected = await client.GetAsync("/test", TestContext.Current.CancellationToken);

        rejected.StatusCode.Should().Be((HttpStatusCode)429);
        rejected.Headers.Contains("Retry-After").Should().BeTrue();
        rejected.Headers.CacheControl!.NoStore.Should().BeTrue();
    }

    /// <summary>
    /// Quota headers on 429 default to off (they disclose the quota to unauthenticated callers). Both
    /// limiters read this one setting.
    /// </summary>
    [Fact]
    public void EmitRateLimitHeadersOnRejection_DefaultsToFalse()
    {
        new ApiPipelineRateLimitingOptions().EmitRateLimitHeadersOnRejection.Should().BeFalse();
    }

    private static Dictionary<string, string?> TokenBucket(int bucketSize, int tokensPerPeriod, int periodSeconds) =>
        TestAppBuilder.WithRateLimiting(permitLimit: bucketSize, windowSeconds: periodSeconds, kind: "TokenBucket")
            .Also(c =>
            {
                c["ApiPipeline:RateLimiting:Policies:0:TokensPerPeriod"] = tokensPerPeriod.ToString(System.Globalization.CultureInfo.InvariantCulture);
                c["ApiPipeline:RateLimiting:EmitRateLimitHeaders"] = "true";
                c["ApiPipeline:RateLimiting:EmitRateLimitHeadersOnRejection"] = "true";
            });

    /// <summary>
    /// A token bucket's <c>PermitLimit</c> is its burst size; what it sustains is
    /// <c>TokensPerPeriod</c> per <c>Window</c>. A client that paces itself from <c>q</c> would send
    /// the burst size every window and be throttled, so <c>q</c> is the sustained rate.
    /// </summary>
    [Fact]
    public async Task TokenBucket_AdvertisesTheSustainedRate_OnSuccess()
    {
        await using var app = await TestAppBuilder.CreateAppAsync(
            TokenBucket(bucketSize: 50, tokensPerPeriod: 10, periodSeconds: 30), addProblemDetails: true);
        app.UseApiPipeline(pipeline => pipeline.WithRateLimiting());
        app.MapGet("/test", () => Results.Ok("ok"));
        await app.StartAsync(TestContext.Current.CancellationToken);

        var response = await app.GetTestClient().GetAsync("/test", TestContext.Current.CancellationToken);

        RateLimitHeaders.PolicyValues(response).Should().Equal("\"strict\";q=10;w=30");
    }

    /// <summary>The 429 advertises the same sustained rate as a success.</summary>
    [Fact]
    public async Task TokenBucket_AdvertisesTheSustainedRate_OnRejection()
    {
        await using var app = await TestAppBuilder.CreateAppAsync(
            TokenBucket(bucketSize: 2, tokensPerPeriod: 1, periodSeconds: 60), addProblemDetails: true);
        app.UseApiPipeline(pipeline => pipeline.WithRateLimiting());
        app.MapGet("/test", () => Results.Ok("ok"));
        await app.StartAsync(TestContext.Current.CancellationToken);
        var client = app.GetTestClient();

        await client.GetAsync("/test", TestContext.Current.CancellationToken);
        await client.GetAsync("/test", TestContext.Current.CancellationToken);
        var rejected = await client.GetAsync("/test", TestContext.Current.CancellationToken);

        rejected.StatusCode.Should().Be((HttpStatusCode)429);
        RateLimitHeaders.PolicyValues(rejected).Should().Equal("\"strict\";q=1;w=60");
    }

    /// <summary>
    /// .NET's in-memory sliding window reports no <c>RetryAfter</c>. Its oldest segment slides out
    /// within one segment, the earliest a permit can come back, so that is the <c>Retry-After</c>.
    /// </summary>
    [Fact]
    public async Task SlidingWindow_Rejection_CarriesRetryAfter_OfOneSegment()
    {
        var config = TestAppBuilder.WithRateLimiting(permitLimit: 1, windowSeconds: 60, kind: "SlidingWindow")
            .Also(c => c["ApiPipeline:RateLimiting:Policies:0:SegmentsPerWindow"] = "4");
        await using var app = await TestAppBuilder.CreateAppAsync(config, addProblemDetails: true);
        app.UseApiPipeline(pipeline => pipeline.WithRateLimiting());
        app.MapGet("/test", () => Results.Ok("ok"));
        await app.StartAsync(TestContext.Current.CancellationToken);
        var client = app.GetTestClient();

        await client.GetAsync("/test", TestContext.Current.CancellationToken);
        var rejected = await client.GetAsync("/test", TestContext.Current.CancellationToken);

        rejected.StatusCode.Should().Be((HttpStatusCode)429);
        rejected.Headers.RetryAfter!.Delta.Should().Be(TimeSpan.FromSeconds(15));
    }

    /// <summary>
    /// An application that sets its own <c>OnRejected</c> (to log, say) after the package replaces
    /// the package's callback, and the framework's default <c>RejectionStatusCode</c> is 503. The
    /// package sets 429, so a rejection stays a 429 whoever writes the response.
    /// </summary>
    [Fact]
    public async Task Rejection_Is429_WhenTheApplicationReplacesOnRejected()
    {
        await using var app = await TestAppBuilder.CreateAppAsync(
            TestAppBuilder.WithRateLimiting(permitLimit: 1, windowSeconds: 60),
            addProblemDetails: true,
            configureServices: s => s.AddRateLimiter(o => o.OnRejected = (_, _) => ValueTask.CompletedTask));
        app.UseApiPipeline(pipeline => pipeline.WithRateLimiting());
        app.MapGet("/test", () => Results.Ok("ok"));
        await app.StartAsync(TestContext.Current.CancellationToken);
        var client = app.GetTestClient();

        await client.GetAsync("/test", TestContext.Current.CancellationToken);
        var rejected = await client.GetAsync("/test", TestContext.Current.CancellationToken);

        rejected.StatusCode.Should().Be((HttpStatusCode)429);
    }
}

/// <summary>Small helper for composing a configuration dictionary inline.</summary>
internal static class DictionaryComposition
{
    /// <summary>Applies <paramref name="configure"/> and returns the same dictionary.</summary>
    public static Dictionary<string, string?> Also(
        this Dictionary<string, string?> config,
        Action<Dictionary<string, string?>> configure)
    {
        configure(config);
        return config;
    }
}
