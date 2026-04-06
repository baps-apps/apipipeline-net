using ApiPipeline.NET.Extensions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ApiPipeline.NET.Tests;

internal static class TestAppBuilder
{
    public static async Task<WebApplication> CreateAppAsync(
        Dictionary<string, string?> config,
        string? environment = null,
        bool addExceptionHandler = false)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = environment ?? Environments.Development
        });

        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(config);

        builder.Services.AddRouting();
        builder.Services.AddAuthentication(defaultScheme: "Test")
            .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, TestAuthHandler>("Test", _ => { });
        builder.Services.AddAuthorization();
        builder.Services
            .AddCorrelationId()
            .AddRateLimiting(builder.Configuration)
            .AddResponseCompression(builder.Configuration)
            .AddResponseCaching(builder.Configuration)
            .AddSecurityHeaders(builder.Configuration)
            .AddCors(builder.Configuration)
            .AddApiVersionDeprecation(builder.Configuration)
            .AddRequestLimits(builder.Configuration)
            .AddForwardedHeaders(builder.Configuration);

        if (addExceptionHandler)
        {
            builder.Services.AddApiPipelineExceptionHandler();
        }

        builder.ConfigureKestrelRequestLimits();

        var app = builder.Build();
        await Task.Yield();
        return app;
    }

    public static Dictionary<string, string?> MinimalConfig(Action<Dictionary<string, string?>>? configure = null)
    {
        var config = new Dictionary<string, string?>
        {
            ["RateLimitingOptions:Enabled"] = "false",
            ["ResponseCompressionOptions:Enabled"] = "false",
            ["ResponseCachingOptions:Enabled"] = "false",
            ["SecurityHeaders:Enabled"] = "false",
            ["CorsOptions:Enabled"] = "false",
            ["ApiVersionDeprecationOptions:Enabled"] = "false",
            ["ForwardedHeadersOptions:Enabled"] = "true"
        };

        configure?.Invoke(config);
        return config;
    }

    public static Dictionary<string, string?> WithRateLimiting(
        string defaultPolicy = "strict",
        int permitLimit = 5,
        int windowSeconds = 60,
        string kind = "FixedWindow")
    {
        return MinimalConfig(config =>
        {
            config["RateLimitingOptions:Enabled"] = "true";
            config["RateLimitingOptions:DefaultPolicy"] = defaultPolicy;
            config["RateLimitingOptions:Policies:0:Name"] = defaultPolicy;
            config["RateLimitingOptions:Policies:0:Kind"] = kind;
            config["RateLimitingOptions:Policies:0:PermitLimit"] = permitLimit.ToString();
            config["RateLimitingOptions:Policies:0:WindowSeconds"] = windowSeconds.ToString();
            config["RateLimitingOptions:Policies:0:QueueLimit"] = "0";
            config["RateLimitingOptions:Policies:0:QueueProcessingOrder"] = "OldestFirst";
        });
    }

    public static Dictionary<string, string?> WithSecurityHeaders(bool enabled = true)
    {
        return MinimalConfig(config =>
        {
            config["SecurityHeaders:Enabled"] = enabled.ToString().ToLowerInvariant();
        });
    }
}
