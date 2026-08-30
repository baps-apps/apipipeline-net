using System.Net;
using ApiPipeline.NET.Pipeline;
using FluentAssertions;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Options;

namespace ApiPipeline.NET.Tests;

/// <summary>
/// Options set in code are first-class: everything the package reads comes from the validated
/// options, never from raw <c>IConfiguration</c>, so <c>Configure&lt;T&gt;</c> and the
/// <c>Action&lt;TOptions&gt;</c> overloads behave exactly like configuration.
/// </summary>
public sealed class OptionsPatternTests
{
    /// <summary>
    /// Fails if named policies are registered by reading <c>IConfiguration</c> directly: a policy
    /// added in code then never reaches the rate limiter, and <c>RequireRateLimiting</c> throws.
    /// </summary>
    [Fact]
    public async Task PolicyAddedWithConfigure_IsUsableByRequireRateLimiting()
    {
        await using var app = await TestAppBuilder.CreateAppAsync(
            TestAppBuilder.WithRateLimiting(defaultPolicy: "standard", permitLimit: 100),
            configureServices: s => s.Configure<ApiPipelineRateLimitingOptions>(o => o.Policies.Add(
                new ApiPipelineRateLimitPolicy { Name = "burst", PermitLimit = 1, Window = TimeSpan.FromSeconds(60) })));
        app.UseApiPipeline(pipeline => pipeline.WithRateLimiting());
        app.MapGet("/burst", () => "ok").RequireRateLimiting("burst");
        await app.StartAsync(TestContext.Current.CancellationToken);
        var client = app.GetTestClient();

        (await client.GetAsync("/burst", TestContext.Current.CancellationToken)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/burst", TestContext.Current.CancellationToken)).StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
    }

    /// <summary>
    /// The <c>Action&lt;TOptions&gt;</c> overload configures a feature with no configuration at all.
    /// </summary>
    [Fact]
    public async Task ActionOverload_ConfiguresRateLimiting_WithoutConfiguration()
    {
        await using var app = await TestAppBuilder.CreateAppAsync(
            TestAppBuilder.MinimalConfig(),
            configureServices: s => s.AddApiPipelineRateLimiting(o =>
            {
                o.Enabled = true;
                o.DefaultPolicy = "code";
                o.AnonymousFallback = AnonymousFallbackBehavior.RateLimit;
                o.Policies.Add(new ApiPipelineRateLimitPolicy { Name = "code", PermitLimit = 1, Window = TimeSpan.FromSeconds(60) });
            }));
        app.UseApiPipeline(pipeline => pipeline.WithRateLimiting());
        app.MapGet("/test", () => "ok");
        await app.StartAsync(TestContext.Current.CancellationToken);
        var client = app.GetTestClient();

        (await client.GetAsync("/test", TestContext.Current.CancellationToken)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/test", TestContext.Current.CancellationToken)).StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
    }

    /// <summary>
    /// The two-caches check must see output caching enabled in code, not only in configuration.
    /// </summary>
    [Fact]
    public async Task ResponseAndOutputCaching_BothEnabled_OneInCode_FailsStartup()
    {
        await using var app = await TestAppBuilder.CreateAppAsync(
            TestAppBuilder.MinimalConfig(c => c["ApiPipeline:ResponseCaching:Enabled"] = "true"),
            configureServices: s => s.Configure<ApiPipelineOutputCachingOptions>(o => o.Enabled = true));
        app.MapGet("/test", () => "ok");

        var act = () => app.StartAsync();

        (await act.Should().ThrowAsync<OptionsValidationException>())
            .WithMessage("*ApiPipeline:OutputCaching:Enabled*");
    }

    /// <summary>
    /// Each policy is validated too (<c>ValidateDataAnnotations</c> does not recurse), so
    /// <c>PermitLimit: 0</c> fails startup.
    /// </summary>
    [Fact]
    public async Task PolicyWithZeroPermitLimit_FailsStartup()
    {
        var config = TestAppBuilder.WithRateLimiting(permitLimit: 5);
        config["ApiPipeline:RateLimiting:Policies:0:PermitLimit"] = "0";
        await using var app = await TestAppBuilder.CreateAppAsync(config);
        app.MapGet("/test", () => "ok");

        var act = () => app.StartAsync();

        (await act.Should().ThrowAsync<OptionsValidationException>())
            .WithMessage("*PermitLimit*");
    }
}
