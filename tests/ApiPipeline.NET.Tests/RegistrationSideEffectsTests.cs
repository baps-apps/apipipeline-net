using System.IO.Compression;
using ApiPipeline.NET.Cors;
using System.Threading.RateLimiting;
using FluentAssertions;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.HttpsPolicy;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.ResponseCaching;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Options;

namespace ApiPipeline.NET.Tests;

/// <summary>
/// <c>AddApiPipeline</c> registers every feature, so a feature that is off must leave the
/// application's own setup for the same framework feature alone: its CORS provider, rate limiter,
/// compression, response caching and HSTS settings. And with CORS on, an application's own
/// <see cref="ICorsPolicyProvider"/> is wrapped, not discarded.
/// </summary>
public sealed class RegistrationSideEffectsTests
{
    private const string AppOrigin = "https://app-provider.example";
    private const string ConfiguredOrigin = "https://configured.example";

    /// <summary>The application registers its services first, then calls <c>AddApiPipeline</c>.</summary>
    private static WebApplication Build(
        Dictionary<string, string?> config,
        Action<IServiceCollection> applicationServices,
        string environment = "Production",
        CapturingLoggerProvider? logs = null,
        Action<IServiceCollection>? afterPipeline = null)
    {
        // Forwarded headers on (the MinimalConfig default) would fail a non-Development start
        // without a trust set; these tests are not about it.
        config["ApiPipeline:ForwardedHeaders:Enabled"] = "false";

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment });
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(config);
        if (logs is not null)
        {
            builder.Logging.AddProvider(logs);
        }

        applicationServices(builder.Services);
        builder.Services.AddApiPipeline(builder.Configuration);
        afterPipeline?.Invoke(builder.Services);
        return builder.Build();
    }

    private static Dictionary<string, string?> Config(Action<Dictionary<string, string?>>? configure = null) =>
        TestAppBuilder.MinimalConfig(configure);

    private static Dictionary<string, string?> CorsEnabled() => Config(c =>
    {
        c["ApiPipeline:Cors:Enabled"] = "true";
        c["ApiPipeline:Cors:AllowedOrigins:0"] = ConfiguredOrigin;
    });

    private static async Task<string?> AllowedOriginAsync(WebApplication app, string origin, string path = "/test")
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("Origin", origin);
        var response = await app.GetTestClient().SendAsync(request, TestContext.Current.CancellationToken);
        return response.Headers.TryGetValues("Access-Control-Allow-Origin", out var values) ? values.Single() : null;
    }

    // ------------------------------------------------------------------ CORS provider

    /// <summary>
    /// With pipeline CORS off, the application's own provider answers every lookup, default included.
    /// </summary>
    [Fact]
    public async Task Cors_Disabled_ApplicationProviderAnswersEverything()
    {
        await using var app = Build(Config(), services =>
        {
            services.AddCors();
            services.AddSingleton<ICorsPolicyProvider, AppCorsPolicyProvider>();
        });
        app.UseCors();
        app.MapGet("/test", () => "ok");
        await app.StartAsync(TestContext.Current.CancellationToken);

        (await AllowedOriginAsync(app, AppOrigin)).Should().Be(AppOrigin);
    }

    /// <summary>
    /// The framework's transient, stateless <see cref="DefaultCorsPolicyProvider"/> is wrapped once,
    /// not on every request.
    /// </summary>
    [Fact]
    public async Task Cors_DefaultProvider_IsWrappedOnce_NotPerRequest()
    {
        await using var app = Build(CorsEnabled(), services => services.AddCors());

        using var first = app.Services.CreateScope();
        using var second = app.Services.CreateScope();
        var provider = first.ServiceProvider.GetRequiredService<ICorsPolicyProvider>();

        provider.Should().BeOfType<ConfiguredCorsPolicyProvider>()
            .Which.Inner.Should().BeOfType<DefaultCorsPolicyProvider>();
        provider.Should().BeSameAs(second.ServiceProvider.GetRequiredService<ICorsPolicyProvider>());
        provider.Should().BeSameAs(first.ServiceProvider.GetRequiredService<ICorsPolicyProvider>());
    }

    /// <summary>
    /// With pipeline CORS on, the default lookup comes from <c>ApiPipeline:Cors</c> and named
    /// lookups still reach the application's provider, at the lifetime it was registered with.
    /// </summary>
    [Fact]
    public async Task Cors_Enabled_WrapsApplicationProvider_AtItsLifetime()
    {
        var logs = new CapturingLoggerProvider();
        await using var app = Build(CorsEnabled(), services =>
        {
            services.AddCors();
            services.AddScoped<ICorsPolicyProvider, AppCorsPolicyProvider>();
        }, logs: logs);
        app.UseRouting();
        app.UseCors();
        app.MapGet("/test", () => "ok");
        app.MapGet("/named", () => "ok").RequireCors(AppCorsPolicyProvider.PolicyName);
        await app.StartAsync(TestContext.Current.CancellationToken);

        (await AllowedOriginAsync(app, ConfiguredOrigin)).Should().Be(ConfiguredOrigin, "the default lookup is the pipeline's");
        (await AllowedOriginAsync(app, AppOrigin)).Should().BeNull("the application's provider does not answer the default lookup");
        (await AllowedOriginAsync(app, AppOrigin, "/named")).Should().Be(AppOrigin, "named lookups reach the application's provider");

        using var first = app.Services.CreateScope();
        using var second = app.Services.CreateScope();
        first.ServiceProvider.GetRequiredService<ICorsPolicyProvider>()
            .Should().NotBeSameAs(second.ServiceProvider.GetRequiredService<ICorsPolicyProvider>(), "the scoped lifetime is kept");

        lock (logs.Messages)
        {
            logs.Messages.Should().Contain(m => m.Contains(nameof(AppCorsPolicyProvider), StringComparison.Ordinal)
                && m.Contains("keeps answering named", StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// A provider registered after <c>AddApiPipeline</c> replaces the pipeline's, so
    /// <c>ApiPipeline:Cors</c> would silently not apply: startup fails outside Development.
    /// </summary>
    [Fact]
    public async Task Cors_Enabled_ProviderRegisteredAfter_FailsStartup()
    {
        await using var app = Build(CorsEnabled(), _ => { },
            afterPipeline: services => services.AddSingleton<ICorsPolicyProvider, AppCorsPolicyProvider>());

        var act = async () => await app.StartAsync(TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage($"*{nameof(AppCorsPolicyProvider)}*registered*after*NOT*applied*");
    }

    /// <summary>The same in Development is a warning, and the application's provider is in charge.</summary>
    [Fact]
    public async Task Cors_Enabled_ProviderRegisteredAfter_WarnsInDevelopment()
    {
        var logs = new CapturingLoggerProvider();
        await using var app = Build(CorsEnabled(), _ => { }, Environments.Development, logs,
            afterPipeline: services => services.AddSingleton<ICorsPolicyProvider, AppCorsPolicyProvider>());

        await app.StartAsync(TestContext.Current.CancellationToken);

        lock (logs.Messages)
        {
            logs.Messages.Should().Contain(m => m.Contains("NOT applied", StringComparison.Ordinal));
        }
    }

    // ------------------------------------------------------------------ framework options

    /// <summary>Rate limiting off: the application's own global limiter and rejection callback survive.</summary>
    [Fact]
    public async Task RateLimiting_Disabled_LeavesApplicationRateLimiterOptions()
    {
        var globalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(_ => RateLimitPartition.GetNoLimiter("app"));
        Func<OnRejectedContext, CancellationToken, ValueTask> onRejected = (_, _) => ValueTask.CompletedTask;

        await using var app = Build(Config(), services => services.AddRateLimiter(o =>
        {
            o.GlobalLimiter = globalLimiter;
            o.OnRejected = onRejected;
        }));

        var options = app.Services.GetRequiredService<IOptions<RateLimiterOptions>>().Value;
        options.GlobalLimiter.Should().BeSameAs(globalLimiter);
        options.OnRejected.Should().BeSameAs(onRejected);
    }

    /// <summary>Compression off: the application's providers and compression level survive.</summary>
    [Fact]
    public async Task ResponseCompression_Disabled_LeavesApplicationCompressionOptions()
    {
        await using var app = Build(Config(), services =>
        {
            services.AddResponseCompression(o =>
            {
                o.EnableForHttps = true;
                o.Providers.Add<GzipCompressionProvider>();
            });
            services.Configure<GzipCompressionProviderOptions>(o => o.Level = CompressionLevel.SmallestSize);
        });

        var options = app.Services.GetRequiredService<IOptions<ResponseCompressionOptions>>().Value;
        options.EnableForHttps.Should().BeTrue();
        options.Providers.Should().ContainSingle();
        app.Services.GetRequiredService<IOptions<GzipCompressionProviderOptions>>().Value.Level
            .Should().Be(CompressionLevel.SmallestSize);
    }

    /// <summary>Response caching off: the application's size limit survives.</summary>
    [Fact]
    public async Task ResponseCaching_Disabled_LeavesApplicationResponseCachingOptions()
    {
        await using var app = Build(Config(), services => services.AddResponseCaching(o =>
        {
            o.SizeLimit = 12_345;
            o.UseCaseSensitivePaths = true;
        }));

        var options = app.Services.GetRequiredService<IOptions<ResponseCachingOptions>>().Value;
        options.SizeLimit.Should().Be(12_345);
        options.UseCaseSensitivePaths.Should().BeTrue();
    }

    /// <summary>Security headers off, or HSTS off: the application's own HSTS settings survive.</summary>
    [Theory]
    [InlineData("ApiPipeline:SecurityHeaders:Enabled")]
    [InlineData("ApiPipeline:SecurityHeaders:EnableStrictTransportSecurity")]
    public async Task Hsts_Disabled_LeavesApplicationHstsOptions(string switchedOff)
    {
        await using var app = Build(
            Config(c =>
            {
                c["ApiPipeline:SecurityHeaders:Enabled"] = "true";
                c[switchedOff] = "false";
            }),
            services => services.AddHsts(o =>
            {
                o.MaxAge = TimeSpan.FromDays(7);
                o.IncludeSubDomains = false;
            }));

        var options = app.Services.GetRequiredService<IOptions<HstsOptions>>().Value;
        options.MaxAge.Should().Be(TimeSpan.FromDays(7));
        options.IncludeSubDomains.Should().BeFalse();
    }

    /// <summary>An application's own provider: one named policy, and a default one.</summary>
    private sealed class AppCorsPolicyProvider : ICorsPolicyProvider
    {
        public const string PolicyName = "app";

        private static readonly CorsPolicy Policy = new CorsPolicyBuilder().WithOrigins(AppOrigin).Build();

        public Task<CorsPolicy?> GetPolicyAsync(HttpContext context, string? policyName) =>
            Task.FromResult<CorsPolicy?>(policyName is null or PolicyName ? Policy : null);
    }
}
