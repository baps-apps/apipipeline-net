using FluentAssertions;
using ApiPipeline.NET.Pipeline;
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
            c["ApiPipeline:RateLimiting:Enabled"] = "true";
            c["ApiPipeline:RateLimiting:DefaultPolicy"] = "strict";
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
            c["ApiPipeline:RateLimiting:Enabled"] = "true";
            c["ApiPipeline:RateLimiting:DefaultPolicy"] = "nonexistent";
            c["ApiPipeline:RateLimiting:Policies:0:Name"] = "strict";
            c["ApiPipeline:RateLimiting:Policies:0:Kind"] = "FixedWindow";
            c["ApiPipeline:RateLimiting:Policies:0:PermitLimit"] = "10";
            c["ApiPipeline:RateLimiting:Policies:0:Window"] = "00:01:00";
            c["ApiPipeline:RateLimiting:Policies:0:QueueLimit"] = "0";
            c["ApiPipeline:RateLimiting:Policies:0:QueueProcessingOrder"] = "OldestFirst";
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
            c["ApiPipeline:Cors:Enabled"] = "true";
            c["ApiPipeline:Cors:AllowCredentials"] = "true";
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
            c["ApiPipeline:Cors:Enabled"] = "true";
            c["ApiPipeline:Cors:AllowCredentials"] = "true";
            c["ApiPipeline:Cors:AllowedOrigins:0"] = "https://example.com";
        });

        await using var app = await TestAppBuilder.CreateAppAsync(config);
        app.MapGet("/test", () => "ok");

        var act = () => app.StartAsync();
        await act.Should().NotThrowAsync();
    }

    /// <summary>
    /// Verifies that enabling CORS with an empty AllowedMethods configuration fails startup validation.
    /// </summary>
    [Fact]
    public async Task Cors_Enabled_With_Empty_AllowedMethods_Fails_Validation()
    {
        var config = TestAppBuilder.MinimalConfig(c =>
        {
            c["ApiPipeline:Cors:Enabled"] = "true";
            c["ApiPipeline:Cors:AllowedMethods:0"] = " ";
            c["ApiPipeline:Cors:AllowedOrigins:0"] = "https://example.com";
        });

        await using var app = await TestAppBuilder.CreateAppAsync(config);
        app.MapGet("/test", () => "ok");

        var act = () => app.StartAsync();
        await act.Should().ThrowAsync<OptionsValidationException>()
            .WithMessage("*AllowedMethods*");
    }

    /// <summary>
    /// Verifies that enabling CORS with an empty AllowedHeaders configuration fails startup validation.
    /// </summary>
    [Fact]
    public async Task Cors_Enabled_With_Empty_AllowedHeaders_Fails_Validation()
    {
        var config = TestAppBuilder.MinimalConfig(c =>
        {
            c["ApiPipeline:Cors:Enabled"] = "true";
            c["ApiPipeline:Cors:AllowedHeaders:0"] = " ";
            c["ApiPipeline:Cors:AllowedOrigins:0"] = "https://example.com";
        });

        await using var app = await TestAppBuilder.CreateAppAsync(config);
        app.MapGet("/test", () => "ok");

        var act = () => app.StartAsync();
        await act.Should().ThrowAsync<OptionsValidationException>()
            .WithMessage("*AllowedHeaders*");
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
            c["ApiPipeline:RateLimiting:Enabled"] = "true";
            c["ApiPipeline:RateLimiting:DefaultPolicy"] = "dup";
            c["ApiPipeline:RateLimiting:Policies:0:Name"] = "dup";
            c["ApiPipeline:RateLimiting:Policies:0:Kind"] = "FixedWindow";
            c["ApiPipeline:RateLimiting:Policies:0:PermitLimit"] = "10";
            c["ApiPipeline:RateLimiting:Policies:0:Window"] = "00:01:00";
            c["ApiPipeline:RateLimiting:Policies:0:QueueLimit"] = "0";
            c["ApiPipeline:RateLimiting:Policies:0:QueueProcessingOrder"] = "OldestFirst";
            c["ApiPipeline:RateLimiting:Policies:1:Name"] = "dup";
            c["ApiPipeline:RateLimiting:Policies:1:Kind"] = "FixedWindow";
            c["ApiPipeline:RateLimiting:Policies:1:PermitLimit"] = "20";
            c["ApiPipeline:RateLimiting:Policies:1:Window"] = "00:00:30";
            c["ApiPipeline:RateLimiting:Policies:1:QueueLimit"] = "0";
            c["ApiPipeline:RateLimiting:Policies:1:QueueProcessingOrder"] = "OldestFirst";
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
            c["ApiPipeline:RateLimiting:Enabled"] = "true";
            c["ApiPipeline:RateLimiting:DefaultPolicy"] = "sliding";
            c["ApiPipeline:RateLimiting:Policies:0:Name"] = "sliding";
            c["ApiPipeline:RateLimiting:Policies:0:Kind"] = "SlidingWindow";
            c["ApiPipeline:RateLimiting:Policies:0:PermitLimit"] = "10";
            c["ApiPipeline:RateLimiting:Policies:0:Window"] = "00:01:00";
            c["ApiPipeline:RateLimiting:Policies:0:QueueLimit"] = "0";
            c["ApiPipeline:RateLimiting:Policies:0:QueueProcessingOrder"] = "OldestFirst";
        });

        await using var app = await TestAppBuilder.CreateAppAsync(config);
        app.MapGet("/test", () => "ok");

        var act = () => app.StartAsync();
        await act.Should().ThrowAsync<OptionsValidationException>()
            .WithMessage("*SegmentsPerWindow*");
    }

    /// <summary>
    /// More than 1000 segments fails validation: each request reads every segment, and a segment
    /// shorter than a millisecond cannot be counted.
    /// </summary>
    [Theory]
    [InlineData("0")]
    [InlineData("1001")]
    [InlineData("2147483647")]
    public async Task RateLimiting_SlidingWindow_SegmentsPerWindow_OutOfRange_Fails_Validation(string segments)
    {
        await using var app = await TestAppBuilder.CreateAppAsync(SlidingWindowConfig(segments));
        app.MapGet("/test", () => "ok");

        var act = () => app.StartAsync();
        await act.Should().ThrowAsync<OptionsValidationException>()
            .WithMessage("*SegmentsPerWindow*");
    }

    /// <summary>The bounds themselves pass; at 1000 segments a one-second window has 1 ms segments.</summary>
    [Theory]
    [InlineData("1")]
    [InlineData("1000")]
    public async Task RateLimiting_SlidingWindow_SegmentsPerWindow_AtBounds_Passes_Validation(string segments)
    {
        await using var app = await TestAppBuilder.CreateAppAsync(SlidingWindowConfig(segments));
        app.MapGet("/test", () => "ok");

        var act = () => app.StartAsync();
        await act.Should().NotThrowAsync();
    }

    private static Dictionary<string, string?> SlidingWindowConfig(string segments) =>
        TestAppBuilder.MinimalConfig(c =>
        {
            c["ApiPipeline:RateLimiting:Enabled"] = "true";
            c["ApiPipeline:RateLimiting:DefaultPolicy"] = "sliding";
            c["ApiPipeline:RateLimiting:Policies:0:Name"] = "sliding";
            c["ApiPipeline:RateLimiting:Policies:0:Kind"] = "SlidingWindow";
            c["ApiPipeline:RateLimiting:Policies:0:PermitLimit"] = "10";
            c["ApiPipeline:RateLimiting:Policies:0:Window"] = "00:00:01";
            c["ApiPipeline:RateLimiting:Policies:0:SegmentsPerWindow"] = segments;
        });

    /// <summary>
    /// Configuration binds an enum from its number too, so <c>"9"</c> binds to a value with no
    /// member: an <c>AnonymousFallback</c> of 3 would act as a shared bucket, a <c>Kind</c> of 9
    /// would skip the window checks and fail every request. A value that is no member fails startup.
    /// </summary>
    [Theory]
    [InlineData("ApiPipeline:RateLimiting:AnonymousFallback", "AnonymousFallback", "Reject, RateLimit, Allow")]
    [InlineData("ApiPipeline:RateLimiting:Policies:0:Kind", "Kind", "FixedWindow, SlidingWindow, Concurrency, TokenBucket")]
    [InlineData("ApiPipeline:RateLimiting:Policies:0:QueueProcessingOrder", "QueueProcessingOrder", "OldestFirst, NewestFirst")]
    public async Task RateLimiting_UndefinedEnumValue_Fails_Validation(string key, string setting, string allowed)
    {
        var config = TestAppBuilder.WithRateLimiting();
        config[key] = "9";

        await using var app = await TestAppBuilder.CreateAppAsync(config);
        app.MapGet("/test", () => "ok");

        var act = () => app.StartAsync();
        await act.Should().ThrowAsync<OptionsValidationException>()
            .WithMessage($"*{setting} (9)*{allowed}*");
    }

    /// <summary>
    /// Verifies that a <c>FixedWindow</c> policy configured without <c>Window</c>
    /// causes an <see cref="OptionsValidationException"/> at startup, because that field
    /// is required to define the window duration.
    /// </summary>
    [Fact]
    public async Task RateLimiting_FixedWindow_Without_Window_Fails_Validation()
    {
        var config = TestAppBuilder.MinimalConfig(c =>
        {
            c["ApiPipeline:RateLimiting:Enabled"] = "true";
            c["ApiPipeline:RateLimiting:DefaultPolicy"] = "strict";
            c["ApiPipeline:RateLimiting:Policies:0:Name"] = "strict";
            c["ApiPipeline:RateLimiting:Policies:0:Kind"] = "FixedWindow";
            c["ApiPipeline:RateLimiting:Policies:0:PermitLimit"] = "10";
            c["ApiPipeline:RateLimiting:Policies:0:QueueLimit"] = "0";
            c["ApiPipeline:RateLimiting:Policies:0:QueueProcessingOrder"] = "OldestFirst";
        });

        await using var app = await TestAppBuilder.CreateAppAsync(config);
        app.MapGet("/test", () => "ok");

        var act = () => app.StartAsync();
        await act.Should().ThrowAsync<OptionsValidationException>()
            .WithMessage("*Window is required*");
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
            c["ApiPipeline:RateLimiting:Enabled"] = "true";
            c["ApiPipeline:RateLimiting:DefaultPolicy"] = "bucket";
            c["ApiPipeline:RateLimiting:Policies:0:Name"] = "bucket";
            c["ApiPipeline:RateLimiting:Policies:0:Kind"] = "TokenBucket";
            c["ApiPipeline:RateLimiting:Policies:0:PermitLimit"] = "100";
            c["ApiPipeline:RateLimiting:Policies:0:Window"] = "00:00:10";
            c["ApiPipeline:RateLimiting:Policies:0:QueueLimit"] = "0";
            c["ApiPipeline:RateLimiting:Policies:0:QueueProcessingOrder"] = "OldestFirst";
        });

        await using var app = await TestAppBuilder.CreateAppAsync(config);
        app.MapGet("/test", () => "ok");

        var act = () => app.StartAsync();
        await act.Should().ThrowAsync<OptionsValidationException>()
            .WithMessage("*TokensPerPeriod*");
    }

    /// <summary>
    /// Verifies that a fully specified <c>TokenBucket</c> policy (including
    /// <c>TokensPerPeriod</c> and <c>Window</c>) passes validation and the
    /// app starts successfully.
    /// </summary>
    [Fact]
    public async Task RateLimiting_TokenBucket_Valid_Config_Passes_Validation()
    {
        var config = TestAppBuilder.MinimalConfig(c =>
        {
            c["ApiPipeline:RateLimiting:Enabled"] = "true";
            c["ApiPipeline:RateLimiting:DefaultPolicy"] = "bucket";
            c["ApiPipeline:RateLimiting:Policies:0:Name"] = "bucket";
            c["ApiPipeline:RateLimiting:Policies:0:Kind"] = "TokenBucket";
            c["ApiPipeline:RateLimiting:Policies:0:PermitLimit"] = "100";
            c["ApiPipeline:RateLimiting:Policies:0:Window"] = "00:00:10";
            c["ApiPipeline:RateLimiting:Policies:0:TokensPerPeriod"] = "20";
            c["ApiPipeline:RateLimiting:Policies:0:QueueLimit"] = "0";
            c["ApiPipeline:RateLimiting:Policies:0:QueueProcessingOrder"] = "OldestFirst";
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
            c["ApiPipeline:ForwardedHeaders:Enabled"] = "false";
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
            c["ApiPipeline:RequestLimits:Enabled"] = "true";
            c["ApiPipeline:RequestLimits:MaxRequestBodySize"] = "0";
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
            c["ApiPipeline:RequestLimits:Enabled"] = "true";
            c["ApiPipeline:RequestLimits:MaxRequestHeadersTotalSize"] = "0";
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
            c["ApiPipeline:RequestLimits:Enabled"] = "true";
            c["ApiPipeline:RequestLimits:MaxRequestBodySize"] = "10485760"; // 10 MB
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
            c["ApiPipeline:ForwardedHeaders:Enabled"] = "true";
            c["ApiPipeline:ForwardedHeaders:ForwardLimit"] = "15";
        });

        await using var app = await TestAppBuilder.CreateAppAsync(config);
        app.MapGet("/test", () => "ok");

        var act = () => app.StartAsync();
        await act.Should().NotThrowAsync();
    }

    /// <summary>
    /// Verifies strict production forwarding posture by failing startup when no trusted proxies/networks
    /// are configured and strict enforcement is enabled.
    /// </summary>
    [Fact]
    public async Task ForwardedHeaders_Production_Without_TrustedProxies_Throws_When_Enforced()
    {
        var config = TestAppBuilder.MinimalConfig(c =>
        {
            c["ApiPipeline:ForwardedHeaders:Enabled"] = "true";
            c["ApiPipeline:ForwardedHeaders:ClearDefaultProxies"] = "false";
            c["ApiPipeline:ForwardedHeaders:EnforceTrustedProxyConfiguration"] = "true";
        });

        await using var app = await TestAppBuilder.CreateAppAsync(config, Environments.Production);
        var act = () =>
        {
            app.UseApiPipeline(pipeline => pipeline.WithForwardedHeaders());
            return Task.CompletedTask;
        };

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*EnforceTrustedProxyConfiguration*");
    }
}
