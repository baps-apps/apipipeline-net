using FluentAssertions;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.Extensions.Options;

namespace ApiPipeline.NET.Tests;

/// <summary>
/// Options as they are after real configuration binding, not as freshly constructed. The binder
/// appends to pre-filled collections, so only binding through the real registration methods can
/// catch a default that cannot be narrowed. <b>Every collection-valued option needs a case here.</b>
/// </summary>
public sealed class ConfigurationBindingTests
{
    private static T Resolve<T>(
        Dictionary<string, string?> config,
        Func<IServiceCollection, IConfiguration, IServiceCollection> register)
        where T : class
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(config).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        register(services, configuration);
        return services.BuildServiceProvider().GetRequiredService<IOptions<T>>().Value;
    }

    /// <summary>
    /// A configured method allow-list is exactly what is allowed. An operator narrowing an API to
    /// read-only cross-origin access must not silently keep the write methods.
    /// </summary>
    [Fact]
    public void Cors_AllowedMethods_ConfiguredList_ReplacesTheSafeDefaults()
    {
        var settings = Resolve<ApiPipelineCorsOptions>(
            new Dictionary<string, string?>
            {
                ["ApiPipeline:Cors:Enabled"] = "true",
                ["ApiPipeline:Cors:AllowedOrigins:0"] = "https://app.example.com",
                ["ApiPipeline:Cors:AllowedMethods:0"] = "GET",
            },
            (s, c) => s.AddApiPipelineCors(c));

        settings.AllowedMethods.Should().Equal("GET");
        settings.AllowedMethods.Should().NotContain("DELETE");
        settings.AllowedMethods.Should().NotContain("POST");
    }

    /// <summary>The same for request headers.</summary>
    [Fact]
    public void Cors_AllowedHeaders_ConfiguredList_ReplacesTheSafeDefaults()
    {
        var settings = Resolve<ApiPipelineCorsOptions>(
            new Dictionary<string, string?>
            {
                ["ApiPipeline:Cors:Enabled"] = "true",
                ["ApiPipeline:Cors:AllowedOrigins:0"] = "https://app.example.com",
                ["ApiPipeline:Cors:AllowedHeaders:0"] = "Content-Type",
            },
            (s, c) => s.AddApiPipelineCors(c));

        settings.AllowedHeaders.Should().Equal("Content-Type");
        settings.AllowedHeaders.Should().NotContain("Authorization");
    }

    /// <summary>
    /// Leaving them unconfigured still applies the safe default sets — the point of the change is
    /// that a configured list wins, not that the defaults are gone.
    /// </summary>
    [Fact]
    public void Cors_UnconfiguredMethodsAndHeaders_StillApplyTheSafeDefaults()
    {
        var settings = Resolve<ApiPipelineCorsOptions>(
            new Dictionary<string, string?>
            {
                ["ApiPipeline:Cors:Enabled"] = "true",
                ["ApiPipeline:Cors:AllowedOrigins:0"] = "https://app.example.com",
            },
            (s, c) => s.AddApiPipelineCors(c));

        settings.AllowedMethods.Should().BeNull();
        settings.AllowedHeaders.Should().BeNull();

        Cors.ConfiguredCorsPolicy.TryBuildConfiguredPolicy(settings, metrics: null, out var policy, out var error)
            .Should().BeTrue(error);
        policy.Methods.Should().BeEquivalentTo(ApiPipelineCorsOptions.SafeDefaultAllowedMethods);
        policy.Headers.Should().BeEquivalentTo(ApiPipelineCorsOptions.SafeDefaultAllowedHeaders);
    }

    /// <summary>
    /// A configured method list reaches the built policy, not just the options object. The options
    /// value and the policy the CORS middleware evaluates are two different things, and only the
    /// second one is what a browser sees.
    /// </summary>
    [Fact]
    public void Cors_ConfiguredMethods_ReachTheBuiltPolicy()
    {
        var settings = Resolve<ApiPipelineCorsOptions>(
            new Dictionary<string, string?>
            {
                ["ApiPipeline:Cors:Enabled"] = "true",
                ["ApiPipeline:Cors:AllowedOrigins:0"] = "https://app.example.com",
                ["ApiPipeline:Cors:AllowedMethods:0"] = "GET",
                ["ApiPipeline:Cors:AllowedMethods:1"] = "HEAD",
            },
            (s, c) => s.AddApiPipelineCors(c));

        Cors.ConfiguredCorsPolicy.TryBuildConfiguredPolicy(settings, metrics: null, out var policy, out var error)
            .Should().BeTrue(error);
        policy.Methods.Should().BeEquivalentTo(["GET", "HEAD"]);
        policy.Methods.Should().NotContain("DELETE");
    }

    /// <summary>
    /// The edge limiter's probe exclusions are replaceable, so a deployment whose probe lives
    /// somewhere other than <c>/health</c> can say so — and one that wants no exclusions at all can
    /// have none.
    /// </summary>
    [Fact]
    public void EdgeRateLimiting_ExcludedPaths_ConfiguredList_ReplacesTheDefault()
    {
        var settings = Resolve<ApiPipelineEdgeRateLimitingOptions>(
            new Dictionary<string, string?>
            {
                ["ApiPipeline:EdgeRateLimiting:Enabled"] = "true",
                ["ApiPipeline:EdgeRateLimiting:ExcludedPaths:0"] = "/probes/live",
            },
            (s, c) => s.AddApiPipelineEdgeRateLimiting(c));

        settings.ExcludedPaths.Should().Equal("/probes/live");
        settings.ExcludedPaths.Should().NotContain("/health");
    }

    /// <summary>The same for the compression exclusions.</summary>
    [Fact]
    public void ResponseCompression_ExcludedPaths_ConfiguredList_ReplacesTheDefault()
    {
        var settings = Resolve<ApiPipelineResponseCompressionOptions>(
            new Dictionary<string, string?>
            {
                ["ApiPipeline:ResponseCompression:Enabled"] = "true",
                ["ApiPipeline:ResponseCompression:ExcludedPaths:0"] = "/metrics",
            },
            (s, c) => s.AddApiPipelineResponseCompression(c));

        settings.ExcludedPaths.Should().Equal("/metrics");
        settings.ExcludedPaths.Should().NotContain("/health");
    }

    /// <summary>
    /// Calling both the aggregate and a per-feature registration binds the section once, so
    /// <see cref="ApiPipelineRateLimitingOptions.Policies"/> is not duplicated.
    /// </summary>
    [Fact]
    public void AggregateAndPerFeatureRegistration_BindTheSectionOnce()
    {
        var options = Resolve<ApiPipelineRateLimitingOptions>(
            RateLimitingConfig(),
            (s, c) => s.AddApiPipeline(c).AddApiPipelineRateLimiting(c));

        options.Policies.Select(p => p.Name).Should().Equal("strict");
    }

    /// <summary>Calling the aggregate registration twice binds the section once.</summary>
    [Fact]
    public void AggregateRegistration_CalledTwice_BindsTheSectionOnce()
    {
        var options = Resolve<ApiPipelineRateLimitingOptions>(
            RateLimitingConfig(),
            (s, c) => s.AddApiPipeline(c).AddApiPipeline(c));

        options.Policies.Select(p => p.Name).Should().Equal("strict");
    }

    /// <summary>
    /// Repeating a per-feature registration binds once, so
    /// <see cref="ApiPipelineForwardedHeadersOptions.KnownNetworks"/> is not duplicated.
    /// </summary>
    [Fact]
    public void PerFeatureRegistration_CalledTwice_BindsTheSectionOnce()
    {
        var settings = Resolve<ApiPipelineForwardedHeadersOptions>(
            new Dictionary<string, string?>
            {
                ["ApiPipeline:ForwardedHeaders:Enabled"] = "true",
                ["ApiPipeline:ForwardedHeaders:KnownNetworks:0"] = "10.0.0.0/8",
            },
            (s, c) => s.AddApiPipelineForwardedHeaders(c).AddApiPipelineForwardedHeaders(c));

        settings.KnownNetworks.Should().Equal("10.0.0.0/8");
    }

    private static Dictionary<string, string?> RateLimitingConfig() => new()
    {
        ["ApiPipeline:RateLimiting:Enabled"] = "true",
        ["ApiPipeline:RateLimiting:DefaultPolicy"] = "strict",
        ["ApiPipeline:RateLimiting:Policies:0:Name"] = "strict",
        ["ApiPipeline:RateLimiting:Policies:0:Kind"] = "FixedWindow",
        ["ApiPipeline:RateLimiting:Policies:0:PermitLimit"] = "10",
        ["ApiPipeline:RateLimiting:Policies:0:Window"] = "00:01:00",
    };
}

/// <summary>
/// Who answers the default CORS lookup: the pipeline's policy when enabled, and the application's
/// own <c>AddDefaultPolicy(...)</c> when <c>ApiPipeline:Cors:Enabled=false</c>.
/// </summary>
public sealed class CorsProviderOwnershipTests
{
    private static async Task<ICorsPolicyProvider> ProviderAsync(bool pipelineCorsEnabled)
    {
        var config = TestAppBuilder.MinimalConfig(c =>
        {
            c["ApiPipeline:Cors:Enabled"] = pipelineCorsEnabled ? "true" : "false";
            c["ApiPipeline:Cors:AllowedOrigins:0"] = "https://pipeline.example.com";
        });

        var app = await TestAppBuilder.CreateAppAsync(
            config,
            configureServices: services => services.AddCors(options =>
                options.AddDefaultPolicy(policy => policy.WithOrigins("https://application.example.com"))));

        return app.Services.GetRequiredService<ICorsPolicyProvider>();
    }

    /// <summary>
    /// With the feature off, the application's own default policy is the one in play. This is the
    /// documented way to keep it, so it has to be the way it actually behaves.
    /// </summary>
    [Fact]
    public async Task Disabled_DelegatesTheDefaultLookupToTheApplicationsOwnPolicy()
    {
        var provider = await ProviderAsync(pipelineCorsEnabled: false);

        var policy = await provider.GetPolicyAsync(new DefaultHttpContext(), policyName: null);

        policy.Should().NotBeNull("ApiPipeline:Cors:Enabled=false must hand the default lookup back, not answer it with null");
        policy!.Origins.Should().Equal("https://application.example.com");
    }

    /// <summary>
    /// With the feature on, the pipeline's configured policy wins — that override is the whole
    /// point of the feature, and <c>CorsOverrideStartupGuard</c> is what makes it loud.
    /// </summary>
    [Fact]
    public async Task Enabled_AnswersTheDefaultLookupWithTheConfiguredPolicy()
    {
        var provider = await ProviderAsync(pipelineCorsEnabled: true);

        var policy = await provider.GetPolicyAsync(new DefaultHttpContext(), policyName: null);

        policy!.Origins.Should().Equal("https://pipeline.example.com");
    }
}
