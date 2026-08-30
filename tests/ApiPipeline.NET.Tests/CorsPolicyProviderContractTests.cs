using ApiPipeline.NET.Cors;
using ApiPipeline.NET.Pipeline;
using FluentAssertions;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;

namespace ApiPipeline.NET.Tests;

/// <summary>
/// Contract tests for <see cref="ConfiguredCorsPolicyProvider"/>, the host's only
/// <see cref="ICorsPolicyProvider"/>: it must honour named policies and
/// <see cref="ApiPipelineCorsOptions.Enabled"/>, not always return its own policy.
/// </summary>
public sealed class CorsPolicyProviderContractTests
{
    private static ConfiguredCorsPolicyProvider CreateProvider(
        ApiPipelineCorsOptions settings,
        string environment = "Production",
        Action<CorsOptions>? configureNamedPolicies = null)
    {
        var corsOptions = new CorsOptions();
        configureNamedPolicies?.Invoke(corsOptions);

        return new ConfiguredCorsPolicyProvider(
            new ConfiguredCorsPolicy(
                Options.Create(settings),
                new TestEnvironment(environment),
                TestMetrics.Unobserved),
            new DefaultCorsPolicyProvider(Options.Create(corsOptions)),
            ownsInner: true);
    }

    /// <summary>
    /// A named policy registered by the consuming application must be returned as-is. Returning the
    /// pipeline's own policy would silently replace every <c>[EnableCors("name")]</c> in the app.
    /// </summary>
    [Fact]
    public async Task Cors_NamedPolicy_RegisteredByConsumer_IsNotOverriddenByPipelinePolicy()
    {
        var provider = CreateProvider(
            new ApiPipelineCorsOptions { Enabled = true, AllowedOrigins = ["https://pipeline.example.com"] },
            configureNamedPolicies: o => o.AddPolicy(
                "consumer",
                b => b.WithOrigins("https://consumer.example.com")));

        var policy = await provider.GetPolicyAsync(new DefaultHttpContext(), "consumer");

        policy.Should().NotBeNull();
        policy!.Origins.Should().Contain("https://consumer.example.com");
        policy.Origins.Should().NotContain("https://pipeline.example.com");
    }

    /// <summary>
    /// An unknown policy name must resolve to null so the CORS middleware reports a missing policy,
    /// rather than silently applying the pipeline policy under someone else's name.
    /// </summary>
    [Fact]
    public async Task Cors_UnknownNamedPolicy_ReturnsNull()
    {
        var provider = CreateProvider(new ApiPipelineCorsOptions { Enabled = true, AllowedOrigins = ["https://a.example.com"] });

        var policy = await provider.GetPolicyAsync(new DefaultHttpContext(), "does-not-exist");

        policy.Should().BeNull();
    }

    /// <summary>
    /// The default (unnamed) lookup must return the pipeline policy when CORS is enabled.
    /// </summary>
    [Fact]
    public async Task Cors_DefaultLookup_ReturnsPipelinePolicy_WhenEnabled()
    {
        var provider = CreateProvider(new ApiPipelineCorsOptions { Enabled = true, AllowedOrigins = ["https://a.example.com"] });

        var policy = await provider.GetPolicyAsync(new DefaultHttpContext(), null);

        policy.Should().NotBeNull();
        policy!.Origins.Should().Contain("https://a.example.com");
    }

    /// <summary>
    /// When CORS is disabled the provider must return null, so a stray framework
    /// <c>UseApiPipelineCors()</c> or <c>[EnableCors]</c> cannot resurrect a policy the operator turned off.
    /// </summary>
    [Fact]
    public async Task Cors_DefaultLookup_ReturnsNull_WhenDisabled()
    {
        var provider = CreateProvider(new ApiPipelineCorsOptions { Enabled = false, AllowedOrigins = ["https://a.example.com"] });

        var policy = await provider.GetPolicyAsync(new DefaultHttpContext(), null);

        policy.Should().BeNull();
    }

    /// <summary>
    /// Wildcard origin combined with credentials is rejected by the CORS specification and throws
    /// from <c>CorsPolicyBuilder.Build()</c>. Options validation must catch it at startup rather
    /// than letting a healthy-looking pod fail on its first CORS request.
    /// </summary>
    [Fact]
    public async Task Cors_AllowCredentials_With_Wildcard_Origin_FailsStartup()
    {
        var config = TestAppBuilder.MinimalConfig(c =>
        {
            c["ApiPipeline:Cors:Enabled"] = "true";
            c["ApiPipeline:Cors:AllowCredentials"] = "true";
            c["ApiPipeline:Cors:AllowedOrigins:0"] = "*";
        });

        await using var app = await TestAppBuilder.CreateAppAsync(config, Environments.Production);
        app.MapGet("/test", () => "ok");

        var act = () => app.StartAsync();

        await act.Should().ThrowAsync<OptionsValidationException>()
            .WithMessage("*wildcard*");
    }

    /// <summary>
    /// Any configuration that <c>CorsPolicyBuilder.Build()</c> rejects must fail at startup, not at
    /// request time — a pod that passes its readiness probe and then 500s on every CORS request is
    /// worse than one that never starts.
    /// </summary>
    [Fact]
    public async Task Cors_ConfigurationThatCannotBuildAPolicy_FailsStartup()
    {
        var config = TestAppBuilder.MinimalConfig(c =>
        {
            c["ApiPipeline:Cors:Enabled"] = "true";
            c["ApiPipeline:Cors:AllowCredentials"] = "true";
            c["ApiPipeline:Cors:AllowedOrigins:0"] = "https://ok.example.com";
            c["ApiPipeline:Cors:AllowedOrigins:1"] = "*";
        });

        await using var app = await TestAppBuilder.CreateAppAsync(config, Environments.Production);
        app.MapGet("/test", () => "ok");

        var act = () => app.StartAsync();

        await act.Should().ThrowAsync<OptionsValidationException>();
    }

    private sealed class TestEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = "/";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

}
