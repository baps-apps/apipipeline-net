using Microsoft.AspNetCore.TestHost;

namespace ApiPipeline.NET.Tests;

internal static class TestAppBuilder
{
    public static async Task<WebApplication> CreateAppAsync(
        Dictionary<string, string?> config,
        string? environment = null,
        bool addProblemDetails = false,
        Action<IServiceCollection>? configureServices = null,
        string? json = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = environment ?? Environments.Development
        });

        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(config);
        if (json is not null)
        {
            // An appsettings.json, for what only JSON can express (an empty list).
            builder.Configuration.AddJsonStream(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json)));
        }

        builder.Services.AddRouting();
        builder.Services.AddAuthentication(defaultScheme: "Test")
            .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, TestAuthHandler>("Test", _ => { });
        builder.Services.AddAuthorization();
        builder.Services
            .AddApiPipelineCorrelationId(builder.Configuration)
            .AddApiPipelineRateLimiting(builder.Configuration)
            .AddApiPipelineResponseCompression(builder.Configuration)
            .AddApiPipelineResponseCaching(builder.Configuration)
            .AddApiPipelineOutputCaching(builder.Configuration)
            .AddApiPipelineSecurityHeaders(builder.Configuration)
            .AddApiPipelineCors(builder.Configuration)
            .AddApiPipelineRequestLimits(builder.Configuration)
            .AddApiPipelineForwardedHeaders(builder.Configuration)
            .AddApiPipelineEdgeRateLimiting(builder.Configuration)
            .AddApiPipelineRequestSizeTracking()
            .AddApiPipelineRequestTimeouts(builder.Configuration);

        if (addProblemDetails)
        {
            // The application-side error handling the README documents, kept in one place so the
            // published snippet stays compiled.
            builder.Services.AddConsumerProblemDetails();
        }

        configureServices?.Invoke(builder.Services);

        var app = builder.Build();
        await Task.Yield();
        return app;
    }

    public static Dictionary<string, string?> MinimalConfig(Action<Dictionary<string, string?>>? configure = null)
    {
        var config = new Dictionary<string, string?>
        {
            ["ApiPipeline:RateLimiting:Enabled"] = "false",
            // TestHost does not set RemoteIpAddress — use shared bucket so rate-limit tests
            // exercise permit limits rather than hitting the null-IP Reject guard.
            ["ApiPipeline:RateLimiting:AnonymousFallback"] = "RateLimit",
            ["ApiPipeline:ResponseCompression:Enabled"] = "false",
            ["ApiPipeline:ResponseCaching:Enabled"] = "false",
            ["ApiPipeline:SecurityHeaders:Enabled"] = "false",
            ["ApiPipeline:Cors:Enabled"] = "false",
            ["ApiPipeline:ForwardedHeaders:Enabled"] = "true",
            ["ApiPipeline:OutputCaching:Enabled"] = "false"
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
            config["ApiPipeline:RateLimiting:Enabled"] = "true";
            config["ApiPipeline:RateLimiting:DefaultPolicy"] = defaultPolicy;
            config["ApiPipeline:RateLimiting:Policies:0:Name"] = defaultPolicy;
            config["ApiPipeline:RateLimiting:Policies:0:Kind"] = kind;
            config["ApiPipeline:RateLimiting:Policies:0:PermitLimit"] = permitLimit.ToString();
            config["ApiPipeline:RateLimiting:Policies:0:Window"] = TimeSpan.FromSeconds(windowSeconds).ToString("c", System.Globalization.CultureInfo.InvariantCulture);
            config["ApiPipeline:RateLimiting:Policies:0:QueueLimit"] = "0";
            config["ApiPipeline:RateLimiting:Policies:0:QueueProcessingOrder"] = "OldestFirst";
        });
    }

    public static Dictionary<string, string?> WithSecurityHeaders(bool enabled = true)
    {
        return MinimalConfig(config =>
        {
            config["ApiPipeline:SecurityHeaders:Enabled"] = enabled.ToString().ToLowerInvariant();
        });
    }
}
