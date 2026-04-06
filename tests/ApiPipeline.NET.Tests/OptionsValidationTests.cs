using ApiPipeline.NET.Extensions;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace ApiPipeline.NET.Tests;

/// <summary>
/// Tests for start-up options validation across ApiPipeline.NET features.
/// Ensures that invalid or incomplete configuration is rejected at startup with an
/// <see cref="OptionsValidationException"/>, and that valid configuration passes through.
/// </summary>
public sealed class OptionsValidationTests
{
    /// <summary>
    /// Verifies that the app starts successfully when rate limiting is disabled,
    /// even if no rate-limiting policies are configured.
    /// </summary>
    [Fact]
    public async Task RateLimiting_Disabled_Does_Not_Require_Policies()
    {
        var config = TestAppBuilder.MinimalConfig();
        await using var app = await TestAppBuilder.CreateAppAsync(config);
        app.MapGet("/test", () => "ok");

        var act = () => app.StartAsync();
        await act.Should().NotThrowAsync();
    }

    /// <summary>
    /// Verifies that enabling rate limiting without defining any policies causes
    /// an <see cref="OptionsValidationException"/> at startup.
    /// </summary>
    [Fact]
    public async Task RateLimiting_Enabled_Without_Policies_Fails_Validation()
    {
        var config = TestAppBuilder.MinimalConfig(c =>
        {
            c["RateLimitingOptions:Enabled"] = "true";
            c["RateLimitingOptions:DefaultPolicy"] = "strict";
        });

        await using var app = await TestAppBuilder.CreateAppAsync(config);
        app.MapGet("/test", () => "ok");

        var act = () => app.StartAsync();
        await act.Should().ThrowAsync<OptionsValidationException>();
    }

    /// <summary>
    /// Verifies that setting <c>DefaultPolicy</c> to a name that does not match any
    /// configured policy causes an <see cref="OptionsValidationException"/> at startup,
    /// and that the exception message references the missing policy name.
    /// </summary>
    [Fact]
    public async Task RateLimiting_DefaultPolicy_Not_Matching_Any_Policy_Fails_Validation()
    {
        var config = TestAppBuilder.MinimalConfig(c =>
        {
            c["RateLimitingOptions:Enabled"] = "true";
            c["RateLimitingOptions:DefaultPolicy"] = "nonexistent";
            c["RateLimitingOptions:Policies:0:Name"] = "strict";
            c["RateLimitingOptions:Policies:0:Kind"] = "FixedWindow";
            c["RateLimitingOptions:Policies:0:PermitLimit"] = "10";
            c["RateLimitingOptions:Policies:0:WindowSeconds"] = "60";
            c["RateLimitingOptions:Policies:0:QueueLimit"] = "0";
            c["RateLimitingOptions:Policies:0:QueueProcessingOrder"] = "OldestFirst";
        });

        await using var app = await TestAppBuilder.CreateAppAsync(config);
        app.MapGet("/test", () => "ok");

        var act = () => app.StartAsync();
        await act.Should().ThrowAsync<OptionsValidationException>()
            .WithMessage("*nonexistent*");
    }

    /// <summary>
    /// Verifies that a complete and consistent rate-limiting configuration
    /// passes validation and the app starts without error.
    /// </summary>
    [Fact]
    public async Task RateLimiting_Valid_Config_Passes_Validation()
    {
        var config = TestAppBuilder.WithRateLimiting();
        await using var app = await TestAppBuilder.CreateAppAsync(config);
        app.MapGet("/test", () => "ok");

        var act = () => app.StartAsync();
        await act.Should().NotThrowAsync();
    }

    /// <summary>
    /// Verifies that enabling CORS with <c>AllowCredentials</c> but without specifying
    /// any <c>AllowedOrigins</c> causes an <see cref="OptionsValidationException"/> at
    /// startup (credentials require explicit origins; wildcard is forbidden by the
    /// CORS specification).
    /// </summary>
    [Fact]
    public async Task Cors_AllowCredentials_Without_Origins_Fails_Validation()
    {
        var config = TestAppBuilder.MinimalConfig(c =>
        {
            c["CorsOptions:Enabled"] = "true";
            c["CorsOptions:AllowCredentials"] = "true";
        });

        await using var app = await TestAppBuilder.CreateAppAsync(config);
        app.MapGet("/test", () => "ok");

        var act = () => app.StartAsync();
        await act.Should().ThrowAsync<OptionsValidationException>()
            .WithMessage("*AllowCredentials*");
    }

    /// <summary>
    /// Verifies that enabling CORS with both <c>AllowCredentials</c> and at least one
    /// explicit <c>AllowedOrigins</c> entry passes validation and the app starts successfully.
    /// </summary>
    [Fact]
    public async Task Cors_AllowCredentials_With_Origins_Passes_Validation()
    {
        var config = TestAppBuilder.MinimalConfig(c =>
        {
            c["CorsOptions:Enabled"] = "true";
            c["CorsOptions:AllowCredentials"] = "true";
            c["CorsOptions:AllowedOrigins:0"] = "https://example.com";
        });

        await using var app = await TestAppBuilder.CreateAppAsync(config);
        app.MapGet("/test", () => "ok");

        var act = () => app.StartAsync();
        await act.Should().NotThrowAsync();
    }

    /// <summary>
    /// Verifies that the app starts without error when the security-headers feature
    /// is disabled (no additional configuration required).
    /// </summary>
    [Fact]
    public async Task SecurityHeaders_Disabled_Passes_Validation()
    {
        var config = TestAppBuilder.MinimalConfig();
        await using var app = await TestAppBuilder.CreateAppAsync(config);
        app.MapGet("/test", () => "ok");

        var act = () => app.StartAsync();
        await act.Should().NotThrowAsync();
    }

    /// <summary>
    /// Verifies that configuring two rate-limiting policies with the same name causes
    /// an <see cref="OptionsValidationException"/> at startup, and that the error message
    /// references the duplicate policy name and uses the word "Duplicate".
    /// </summary>
    [Fact]
    public async Task RateLimiting_DuplicatePolicyNames_Fails_Validation()
    {
        var config = TestAppBuilder.MinimalConfig(c =>
        {
            c["RateLimitingOptions:Enabled"] = "true";
            c["RateLimitingOptions:DefaultPolicy"] = "dup";
            c["RateLimitingOptions:Policies:0:Name"] = "dup";
            c["RateLimitingOptions:Policies:0:Kind"] = "FixedWindow";
            c["RateLimitingOptions:Policies:0:PermitLimit"] = "10";
            c["RateLimitingOptions:Policies:0:WindowSeconds"] = "60";
            c["RateLimitingOptions:Policies:0:QueueLimit"] = "0";
            c["RateLimitingOptions:Policies:0:QueueProcessingOrder"] = "OldestFirst";
            c["RateLimitingOptions:Policies:1:Name"] = "dup";
            c["RateLimitingOptions:Policies:1:Kind"] = "FixedWindow";
            c["RateLimitingOptions:Policies:1:PermitLimit"] = "20";
            c["RateLimitingOptions:Policies:1:WindowSeconds"] = "30";
            c["RateLimitingOptions:Policies:1:QueueLimit"] = "0";
            c["RateLimitingOptions:Policies:1:QueueProcessingOrder"] = "OldestFirst";
        });

        await using var app = await TestAppBuilder.CreateAppAsync(config);
        app.MapGet("/test", () => "ok");

        var act = () => app.StartAsync();
        await act.Should().ThrowAsync<OptionsValidationException>()
            .WithMessage("*Duplicate*dup*");
    }

    /// <summary>
    /// Verifies that a <c>SlidingWindow</c> policy configured without <c>SegmentsPerWindow</c>
    /// causes an <see cref="OptionsValidationException"/> at startup, because that field is
    /// required for the sliding-window algorithm.
    /// </summary>
    [Fact]
    public async Task RateLimiting_SlidingWindow_Without_SegmentsPerWindow_Fails_Validation()
    {
        var config = TestAppBuilder.MinimalConfig(c =>
        {
            c["RateLimitingOptions:Enabled"] = "true";
            c["RateLimitingOptions:DefaultPolicy"] = "sliding";
            c["RateLimitingOptions:Policies:0:Name"] = "sliding";
            c["RateLimitingOptions:Policies:0:Kind"] = "SlidingWindow";
            c["RateLimitingOptions:Policies:0:PermitLimit"] = "10";
            c["RateLimitingOptions:Policies:0:WindowSeconds"] = "60";
            c["RateLimitingOptions:Policies:0:QueueLimit"] = "0";
            c["RateLimitingOptions:Policies:0:QueueProcessingOrder"] = "OldestFirst";
        });

        await using var app = await TestAppBuilder.CreateAppAsync(config);
        app.MapGet("/test", () => "ok");

        var act = () => app.StartAsync();
        await act.Should().ThrowAsync<OptionsValidationException>()
            .WithMessage("*SegmentsPerWindow*");
    }

    /// <summary>
    /// Verifies that a <c>FixedWindow</c> policy configured without <c>WindowSeconds</c>
    /// causes an <see cref="OptionsValidationException"/> at startup, because that field
    /// is required to define the window duration.
    /// </summary>
    [Fact]
    public async Task RateLimiting_FixedWindow_Without_WindowSeconds_Fails_Validation()
    {
        var config = TestAppBuilder.MinimalConfig(c =>
        {
            c["RateLimitingOptions:Enabled"] = "true";
            c["RateLimitingOptions:DefaultPolicy"] = "strict";
            c["RateLimitingOptions:Policies:0:Name"] = "strict";
            c["RateLimitingOptions:Policies:0:Kind"] = "FixedWindow";
            c["RateLimitingOptions:Policies:0:PermitLimit"] = "10";
            c["RateLimitingOptions:Policies:0:QueueLimit"] = "0";
            c["RateLimitingOptions:Policies:0:QueueProcessingOrder"] = "OldestFirst";
        });

        await using var app = await TestAppBuilder.CreateAppAsync(config);
        app.MapGet("/test", () => "ok");

        var act = () => app.StartAsync();
        await act.Should().ThrowAsync<OptionsValidationException>()
            .WithMessage("*WindowSeconds*");
    }

    /// <summary>
    /// Verifies that a <c>TokenBucket</c> policy configured without <c>TokensPerPeriod</c>
    /// causes an <see cref="OptionsValidationException"/> at startup, because that field
    /// is required to define the replenishment rate.
    /// </summary>
    [Fact]
    public async Task RateLimiting_TokenBucket_Without_TokensPerPeriod_Fails_Validation()
    {
        var config = TestAppBuilder.MinimalConfig(c =>
        {
            c["RateLimitingOptions:Enabled"] = "true";
            c["RateLimitingOptions:DefaultPolicy"] = "bucket";
            c["RateLimitingOptions:Policies:0:Name"] = "bucket";
            c["RateLimitingOptions:Policies:0:Kind"] = "TokenBucket";
            c["RateLimitingOptions:Policies:0:PermitLimit"] = "100";
            c["RateLimitingOptions:Policies:0:WindowSeconds"] = "10";
            c["RateLimitingOptions:Policies:0:QueueLimit"] = "0";
            c["RateLimitingOptions:Policies:0:QueueProcessingOrder"] = "OldestFirst";
        });

        await using var app = await TestAppBuilder.CreateAppAsync(config);
        app.MapGet("/test", () => "ok");

        var act = () => app.StartAsync();
        await act.Should().ThrowAsync<OptionsValidationException>()
            .WithMessage("*TokensPerPeriod*");
    }

    /// <summary>
    /// Verifies that a fully specified <c>TokenBucket</c> policy (including
    /// <c>TokensPerPeriod</c> and <c>WindowSeconds</c>) passes validation and the
    /// app starts successfully.
    /// </summary>
    [Fact]
    public async Task RateLimiting_TokenBucket_Valid_Config_Passes_Validation()
    {
        var config = TestAppBuilder.MinimalConfig(c =>
        {
            c["RateLimitingOptions:Enabled"] = "true";
            c["RateLimitingOptions:DefaultPolicy"] = "bucket";
            c["RateLimitingOptions:Policies:0:Name"] = "bucket";
            c["RateLimitingOptions:Policies:0:Kind"] = "TokenBucket";
            c["RateLimitingOptions:Policies:0:PermitLimit"] = "100";
            c["RateLimitingOptions:Policies:0:WindowSeconds"] = "10";
            c["RateLimitingOptions:Policies:0:TokensPerPeriod"] = "20";
            c["RateLimitingOptions:Policies:0:QueueLimit"] = "0";
            c["RateLimitingOptions:Policies:0:QueueProcessingOrder"] = "OldestFirst";
        });

        await using var app = await TestAppBuilder.CreateAppAsync(config);
        app.MapGet("/test", () => "ok");

        var act = () => app.StartAsync();
        await act.Should().NotThrowAsync();
    }

    /// <summary>
    /// Verifies that the app starts without error when forwarded-headers processing
    /// is explicitly disabled.
    /// </summary>
    [Fact]
    public async Task ForwardedHeaders_Disabled_Passes_Validation()
    {
        var config = TestAppBuilder.MinimalConfig(c =>
        {
            c["ForwardedHeadersOptions:Enabled"] = "false";
        });

        await using var app = await TestAppBuilder.CreateAppAsync(config);
        app.MapGet("/test", () => "ok");

        var act = () => app.StartAsync();
        await act.Should().NotThrowAsync();
    }

    /// <summary>
    /// Verifies that MaxRequestBodySize = 0 is rejected at startup when limits are enabled.
    /// A zero-byte body limit silently rejects all POST/PUT/PATCH requests.
    /// </summary>
    [Fact]
    public async Task RequestLimits_MaxRequestBodySize_Zero_Fails_Validation()
    {
        var config = TestAppBuilder.MinimalConfig(c =>
        {
            c["RequestLimitsOptions:Enabled"] = "true";
            c["RequestLimitsOptions:MaxRequestBodySize"] = "0";
        });

        await using var app = await TestAppBuilder.CreateAppAsync(config);
        app.MapGet("/test", () => "ok");

        var act = () => app.StartAsync();
        await act.Should().ThrowAsync<OptionsValidationException>()
            .WithMessage("*MaxRequestBodySize*");
    }

    /// <summary>
    /// Verifies that MaxRequestHeadersTotalSize = 0 is rejected at startup when limits are enabled.
    /// </summary>
    [Fact]
    public async Task RequestLimits_MaxRequestHeadersTotalSize_Zero_Fails_Validation()
    {
        var config = TestAppBuilder.MinimalConfig(c =>
        {
            c["RequestLimitsOptions:Enabled"] = "true";
            c["RequestLimitsOptions:MaxRequestHeadersTotalSize"] = "0";
        });

        await using var app = await TestAppBuilder.CreateAppAsync(config);
        app.MapGet("/test", () => "ok");

        var act = () => app.StartAsync();
        await act.Should().ThrowAsync<OptionsValidationException>()
            .WithMessage("*MaxRequestHeadersTotalSize*");
    }

    /// <summary>
    /// Verifies that a positive MaxRequestBodySize passes validation.
    /// </summary>
    [Fact]
    public async Task RequestLimits_MaxRequestBodySize_Positive_Passes_Validation()
    {
        var config = TestAppBuilder.MinimalConfig(c =>
        {
            c["RequestLimitsOptions:Enabled"] = "true";
            c["RequestLimitsOptions:MaxRequestBodySize"] = "10485760"; // 10 MB
        });

        await using var app = await TestAppBuilder.CreateAppAsync(config);
        app.MapGet("/test", () => "ok");

        var act = () => app.StartAsync();
        await act.Should().NotThrowAsync();
    }

    /// <summary>
    /// Verifies ForwardLimit accepts values up to 20 to support complex proxy topologies
    /// (CloudFront → WAF → ALB → Nginx Ingress → Pod = 4 hops minimum in AWS).
    /// </summary>
    [Fact]
    public async Task ForwardedHeaders_ForwardLimit_15_Passes_Validation()
    {
        var config = TestAppBuilder.MinimalConfig(c =>
        {
            c["ForwardedHeadersOptions:Enabled"] = "true";
            c["ForwardedHeadersOptions:ForwardLimit"] = "15";
        });

        await using var app = await TestAppBuilder.CreateAppAsync(config);
        app.MapGet("/test", () => "ok");

        var act = () => app.StartAsync();
        await act.Should().NotThrowAsync();
    }
}
