using ApiPipeline.NET.Versioning;
using FluentAssertions;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ApiPipeline.NET.Tests;

/// <summary>
/// The package owns one top-level configuration name, <c>ApiPipeline</c>, and can be relocated.
/// </summary>
public sealed class ConfigurationSectionLayoutTests
{
    private static IConfigurationRoot Config(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static int PrefixLength(IServiceCollection services)
    {
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<ApiPipelineRateLimitingOptions>>().Value.IPv6PartitionPrefixLength;
    }

    /// <summary>The application configuration is read through its <c>ApiPipeline</c> section.</summary>
    [Fact]
    public void RootConfiguration_ReadsTheApiPipelineSection()
    {
        var config = Config(new() { ["ApiPipeline:RateLimiting:IPv6PartitionPrefixLength"] = "48" });

        PrefixLength(new ServiceCollection().AddLogging().AddApiPipeline(config)).Should().Be(48);
    }

    /// <summary>
    /// A section is read as the pipeline section itself, so the settings can live anywhere, for
    /// example under a platform-wide parent section.
    /// </summary>
    [Fact]
    public void Section_IsReadAsThePipelineSection()
    {
        var config = Config(new() { ["Platform:Api:RateLimiting:IPv6PartitionPrefixLength"] = "56" });

        PrefixLength(new ServiceCollection().AddLogging().AddApiPipeline(config.GetSection("Platform:Api")))
            .Should().Be(56);
    }

    /// <summary>
    /// The decision is made on the runtime type: a section held in an <see cref="IConfiguration"/>
    /// variable must not be mistaken for a root and searched for a nested <c>ApiPipeline</c>.
    /// </summary>
    [Fact]
    public void SectionTypedAsIConfiguration_IsStillReadAsThePipelineSection()
    {
        IConfiguration section = Config(new() { ["Platform:Api:RateLimiting:IPv6PartitionPrefixLength"] = "56" })
            .GetSection("Platform:Api");

        PrefixLength(new ServiceCollection().AddLogging().AddApiPipelineRateLimiting(section)).Should().Be(56);
    }

    /// <summary>
    /// Top-level <c>*Options</c> sections are not read: a root-level <c>RateLimitingOptions</c> may belong to the application.
    /// </summary>
    [Fact]
    public void LegacyRootSection_IsNotRead()
    {
        var config = Config(new() { ["RateLimitingOptions:IPv6PartitionPrefixLength"] = "48" });

        PrefixLength(new ServiceCollection().AddLogging().AddApiPipeline(config)).Should().Be(64);
    }

    /// <summary>
    /// Top-level <c>*Options</c> sections without their <c>ApiPipeline</c> counterparts leave rate
    /// limiting and CORS silently off. Startup names each such section and where it belongs.
    /// </summary>
    [Fact]
    public async Task LegacyRootSection_WithoutReplacement_WarnsAtStartup()
    {
        var messages = await StartAndCaptureAsync(new()
        {
            ["RateLimitingOptions:Enabled"] = "true",
            ["CorsOptions:Enabled"] = "true",
        });

        messages.Should().ContainSingle(m => m.Contains("1.x top-level sections", StringComparison.Ordinal))
            .Which.Should().Contain("RateLimitingOptions -> ApiPipeline:RateLimiting")
            .And.Contain("CorsOptions -> ApiPipeline:Cors");
    }

    /// <summary>
    /// Once the section has moved, a root-level name of the same spelling is the application's own
    /// and must not be reported.
    /// </summary>
    [Fact]
    public async Task LegacyRootSection_WithReplacement_DoesNotWarn()
    {
        var messages = await StartAndCaptureAsync(new()
        {
            ["CorsOptions:SomethingTheAppOwns"] = "x",
            ["ApiPipeline:Cors:Enabled"] = "false",
        });

        messages.Should().NotContain(m => m.Contains("1.x top-level sections", StringComparison.Ordinal));
    }

    /// <summary>The Versioning package follows the same layout: <c>ApiPipeline:VersionDeprecation</c>.</summary>
    [Fact]
    public void Versioning_ReadsVersionDeprecationUnderTheApiPipelineSection()
    {
        var config = Config(new() { ["ApiPipeline:VersionDeprecation:Enabled"] = "true" });

        using var provider = new ServiceCollection().AddLogging().AddApiPipelineVersionDeprecation(config).BuildServiceProvider();

        provider.GetRequiredService<IOptions<ApiPipelineVersionDeprecationOptions>>().Value.Enabled.Should().BeTrue();
    }

    /// <summary>The Versioning package also accepts a section, read as the pipeline section.</summary>
    [Fact]
    public void Versioning_Section_IsReadAsThePipelineSection()
    {
        var config = Config(new() { ["Platform:Api:VersionDeprecation:Enabled"] = "true" });

        using var provider = new ServiceCollection().AddLogging()
            .AddApiPipelineVersionDeprecation(config.GetSection("Platform:Api")).BuildServiceProvider();

        provider.GetRequiredService<IOptions<ApiPipelineVersionDeprecationOptions>>().Value.Enabled.Should().BeTrue();
    }

    /// <summary>
    /// An unmoved <c>ApiVersionDeprecationOptions</c> section would drop every deprecation header; startup says so.
    /// </summary>
    [Fact]
    public async Task Versioning_LegacyRootSection_WarnsAtStartup()
    {
        var messages = await StartAndCaptureAsync(
            new() { ["ApiVersionDeprecationOptions:Enabled"] = "true" },
            builder => builder.Services.AddApiPipelineVersionDeprecation(builder.Configuration));

        messages.Should().Contain(m =>
            m.Contains("'ApiVersionDeprecationOptions'", StringComparison.Ordinal)
            && m.Contains("ApiPipeline:VersionDeprecation", StringComparison.Ordinal));
    }

    /// <summary>
    /// Registering a feature again with the same configuration is common (<c>AddApiPipeline</c>
    /// plus a per-feature call) and stays a no-op: the section is bound once, so lists such as the
    /// rate limit policies are not duplicated.
    /// </summary>
    [Fact]
    public void SameSection_BoundTwice_IsANoOp()
    {
        var config = Config(new()
        {
            ["ApiPipeline:RateLimiting:Policies:0:Name"] = "p",
            ["ApiPipeline:RateLimiting:Policies:0:PermitLimit"] = "1",
            ["ApiPipeline:RateLimiting:Policies:0:Window"] = "00:00:01",
        });
        var services = new ServiceCollection().AddLogging().AddApiPipeline(config);

        var act = () => services.AddApiPipelineRateLimiting(config.GetSection("ApiPipeline"));

        act.Should().NotThrow();
        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IOptions<ApiPipelineRateLimitingOptions>>().Value.Policies.Should().ContainSingle();
    }

    /// <summary>
    /// A second, different section would be ignored, so the settings an operator put there would
    /// silently not apply. It fails at registration instead, naming both sections.
    /// </summary>
    [Fact]
    public void DifferentSection_BoundToTheSameOptions_Throws()
    {
        var config = Config(new());
        var services = new ServiceCollection().AddLogging().AddApiPipelineCors(config);

        var act = () => services.AddApiPipelineCors(config.GetSection("Tenant"));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*ApiPipelineCorsOptions*'ApiPipeline:Cors'*'Tenant:Cors'*");
    }

    /// <summary>
    /// Passing the feature's own section, the usual Options-pattern habit, would read
    /// <c>ApiPipeline:Cors:Cors</c>: nothing binds and the feature stays off without a word. It
    /// fails at registration instead, naming the section and what to pass.
    /// </summary>
    [Fact]
    public void FeatureOwnSection_Throws()
    {
        var config = Config(new() { ["ApiPipeline:Cors:Enabled"] = "true" });

        var act = () => new ServiceCollection().AddLogging()
            .AddApiPipelineCors(config.GetSection("ApiPipeline:Cors"));

        act.Should().Throw<ArgumentException>()
            .WithParameterName("configuration")
            .WithMessage("*'ApiPipeline:Cors'*'ApiPipeline:Cors:Cors'*GetSection(\"ApiPipeline\")*");
    }

    /// <summary>
    /// Configuration keys are case-insensitive, so the check is too, and it covers every feature
    /// that binds from configuration, not only CORS.
    /// </summary>
    [Fact]
    public void FeatureOwnSection_DifferentCase_Throws()
    {
        var config = Config(new());

        var act = () => new ServiceCollection().AddLogging()
            .AddApiPipelineRateLimiting(config.GetSection("apipipeline:ratelimiting"));

        act.Should().Throw<ArgumentException>().WithParameterName("configuration");
    }

    /// <summary>
    /// The Versioning package rejects its own section like every core feature: read as the
    /// pipeline section, <c>ApiPipeline:VersionDeprecation</c> would bind nothing.
    /// </summary>
    [Theory]
    [InlineData("ApiPipeline:VersionDeprecation")]
    [InlineData("apipipeline:versiondeprecation")]
    public void Versioning_FeatureOwnSection_Throws(string path)
    {
        var config = Config(new());

        var act = () => new ServiceCollection().AddApiPipelineVersionDeprecation(config.GetSection(path));

        act.Should().Throw<ArgumentException>()
            .WithParameterName("configuration")
            .WithMessage($"*'{path}' section itself*'{path}:VersionDeprecation'*GetSection(\"ApiPipeline\")*");
    }

    /// <summary>The Versioning package applies the same rule.</summary>
    [Fact]
    public void Versioning_DifferentSection_Throws()
    {
        var config = Config(new());
        var services = new ServiceCollection().AddApiPipelineVersionDeprecation(config);

        services.Invoking(s => s.AddApiPipelineVersionDeprecation(config)).Should().NotThrow();
        services.Invoking(s => s.AddApiPipelineVersionDeprecation(config.GetSection("Tenant")))
            .Should().Throw<InvalidOperationException>()
            .WithMessage("*'ApiPipeline:VersionDeprecation'*'Tenant:VersionDeprecation'*");
    }

    private static async Task<List<string>> StartAndCaptureAsync(
        Dictionary<string, string?> config,
        Action<WebApplicationBuilder>? configure = null)
    {
        var capture = new CapturingLoggerProvider();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Development });
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(config);
        builder.Services.AddSingleton<ILoggerProvider>(capture);
        builder.Services.AddApiPipeline(builder.Configuration);
        configure?.Invoke(builder);

        await using var app = builder.Build();
        app.MapGet("/test", () => "ok");
        await app.StartAsync(TestContext.Current.CancellationToken);
        return capture.Messages;
    }
}
