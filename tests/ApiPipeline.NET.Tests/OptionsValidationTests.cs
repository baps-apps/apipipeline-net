using ApiPipeline.NET.Extensions;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace ApiPipeline.NET.Tests;

public sealed class OptionsValidationTests
{
    [Fact]
    public async Task RateLimiting_Disabled_Does_Not_Require_Policies()
    {
        var config = TestAppBuilder.MinimalConfig();
        await using var app = await TestAppBuilder.CreateAppAsync(config);
        app.MapGet("/test", () => "ok");

        var act = () => app.StartAsync();
        await act.Should().NotThrowAsync();
    }

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

    [Fact]
    public async Task RateLimiting_Valid_Config_Passes_Validation()
    {
        var config = TestAppBuilder.WithRateLimiting();
        await using var app = await TestAppBuilder.CreateAppAsync(config);
        app.MapGet("/test", () => "ok");

        var act = () => app.StartAsync();
        await act.Should().NotThrowAsync();
    }

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

    [Fact]
    public async Task SecurityHeaders_Disabled_Passes_Validation()
    {
        var config = TestAppBuilder.MinimalConfig();
        await using var app = await TestAppBuilder.CreateAppAsync(config);
        app.MapGet("/test", () => "ok");

        var act = () => app.StartAsync();
        await act.Should().NotThrowAsync();
    }

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
}
