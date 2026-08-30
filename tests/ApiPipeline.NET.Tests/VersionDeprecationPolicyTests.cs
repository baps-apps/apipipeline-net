using ApiPipeline.NET.Versioning;
using Asp.Versioning;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ApiPipeline.NET.Tests;

/// <summary>
/// The package only supplies policy values; <c>Asp.Versioning</c> owns reading, reporting and
/// headers. It adds one <c>IConfigureOptions</c>, so registration order does not matter.
/// </summary>
public sealed class VersionDeprecationPolicyTests
{
    private static WebApplicationBuilder CreateBuilder(bool reportApiVersions, bool deprecationEnabled)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ApiPipeline:VersionDeprecation:Enabled"] = deprecationEnabled ? "true" : "false",
            ["ApiPipeline:VersionDeprecation:DeprecatedVersions:0:Version"] = "1.0",
            ["ApiPipeline:VersionDeprecation:DeprecatedVersions:0:DeprecationDate"] = "2025-07-01T00:00:00Z",
        });
        builder.Services.AddRouting();
        builder.Services.AddApiVersioning(options =>
        {
            options.DefaultApiVersion = new ApiVersion(1, 0);
            options.AssumeDefaultVersionWhenUnspecified = true;
            options.ReportApiVersions = reportApiVersions;
            options.ApiVersionReader = new UrlSegmentApiVersionReader();
        });
        return builder;
    }

    /// <summary>
    /// Registration order does not matter: on a bare collection, before <c>AddApiVersioning</c> or
    /// without it, the call does not throw.
    /// </summary>
    [Fact]
    public void AddApiPipelineVersionDeprecation_BeforeAddApiVersioning_DoesNotThrow()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();

        var act = () => services.AddApiPipelineVersionDeprecation(configuration);

        act.Should().NotThrow(
            "the method only contributes IConfigureOptions<ApiVersioningOptions>, which has no " +
            "dependency on AddApiVersioning having already run");
    }

    /// <summary>A repeat call must stay idempotent — <c>TryAddEnumerable</c> is the guard.</summary>
    [Fact]
    public void AddApiPipelineVersionDeprecation_CalledTwice_RegistersOnePolicyConfigurator()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();

        services.AddApiPipelineVersionDeprecation(configuration);
        services.AddApiPipelineVersionDeprecation(configuration);

        services.Count(d => d.ServiceType == typeof(IConfigureOptions<ApiVersioningOptions>))
            .Should().Be(1);
    }

    /// <summary>Startup succeeds with policies configured and global reporting off.</summary>
    [Fact]
    public async Task ReportApiVersionsFalse_WithPolicies_DoesNotFailStartup()
    {
        await using var app = CreateBuilder(reportApiVersions: false, deprecationEnabled: true)
            .AddApiPipelineVersionDeprecationAndBuild();

        var act = async () => await app.StartAsync();

        await act.Should().NotThrowAsync();
    }

    /// <summary>
    /// Policies configured with global reporting off may emit nothing at all. That silence is
    /// warned about — but never made fatal, because a version set can enable reporting on its own
    /// and that is not observable from options.
    /// </summary>
    [Fact]
    public async Task ReportApiVersionsFalse_WithPolicies_LogsWarning()
    {
        var logs = new CapturingLoggerProvider();
        var builder = CreateBuilder(reportApiVersions: false, deprecationEnabled: true);
        builder.Logging.AddProvider(logs);

        await using var app = builder.AddApiPipelineVersionDeprecationAndBuild();
        await app.StartAsync(TestContext.Current.CancellationToken);

        string.Join("\n", logs.Messages).Should().Contain("ReportApiVersions");
    }

    /// <summary>No policies configured means nothing to warn about.</summary>
    [Fact]
    public async Task ReportApiVersionsFalse_WithoutPolicies_LogsNothing()
    {
        var logs = new CapturingLoggerProvider();
        var builder = CreateBuilder(reportApiVersions: false, deprecationEnabled: false);
        builder.Logging.AddProvider(logs);

        await using var app = builder.AddApiPipelineVersionDeprecationAndBuild();
        await app.StartAsync(TestContext.Current.CancellationToken);

        string.Join("\n", logs.Messages).Should().NotContain("ReportApiVersions");
    }

    /// <summary>
    /// Reporting off globally, on for the endpoint's version set: headers are still emitted, so
    /// nothing in this package may treat the global flag as authoritative.
    /// </summary>
    [Fact]
    public async Task ReportApiVersionsFalse_PerEndpointSetTrue_StillEmitsHeaders()
    {
        await using var app = CreateBuilder(reportApiVersions: false, deprecationEnabled: true)
            .AddApiPipelineVersionDeprecationAndBuild();

        var versionSet = app.NewApiVersionSet()
            .HasDeprecatedApiVersion(new ApiVersion(1, 0))
            .ReportApiVersions()
            .Build();

        app.MapGet("/api/v{version:apiVersion}/orders", () => Results.Ok("v1"))
           .WithApiVersionSet(versionSet)
           .MapToApiVersion(new ApiVersion(1, 0));

        await app.StartAsync(TestContext.Current.CancellationToken);

        var response = await app.GetTestClient().GetAsync("/api/v1/orders", TestContext.Current.CancellationToken);

        response.Headers.Contains("Deprecation").Should()
            .BeTrue("a version set may enable reporting independently of the global flag");
    }

    /// <summary>
    /// A reporter the application registered before <c>AddApiPipelineVersionDeprecation</c> runs unchanged:
    /// it is neither wrapped nor replaced.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CustomReportApiVersions_IsInvoked_WhicheverOrderItIsRegistered(bool registerFirst)
    {
        var builder = CreateBuilder(reportApiVersions: true, deprecationEnabled: true);

        if (registerFirst)
        {
            builder.Services.AddSingleton<IReportApiVersions, MarkerReporter>();
            builder.Services.AddApiPipelineVersionDeprecation(builder.Configuration);
        }
        else
        {
            builder.Services.AddApiPipelineVersionDeprecation(builder.Configuration);
            builder.Services.AddSingleton<IReportApiVersions, MarkerReporter>();
        }

        await using var app = builder.Build();
        var versionSet = app.NewApiVersionSet()
            .HasDeprecatedApiVersion(new ApiVersion(1, 0))
            .Build();
        app.MapGet("/api/v{version:apiVersion}/orders", () => Results.Ok("v1"))
           .WithApiVersionSet(versionSet)
           .MapToApiVersion(new ApiVersion(1, 0));

        await app.StartAsync(TestContext.Current.CancellationToken);
        var response = await app.GetTestClient().GetAsync("/api/v1/orders", TestContext.Current.CancellationToken);

        response.Headers.Contains("X-Marker").Should()
            .BeTrue("the application's reporter is the only IReportApiVersions in play");
    }

    /// <summary>
    /// A <c>Scoped</c> reporter must not break container validation. Nothing in this package
    /// resolves <see cref="IReportApiVersions"/> any more, so both registration orders must build
    /// and start; a singleton dependency on it would fail with "Cannot consume scoped service".
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ScopedCustomReportApiVersions_Builds_WhicheverOrderItIsRegistered(bool registerFirst)
    {
        var builder = CreateBuilder(reportApiVersions: true, deprecationEnabled: true);

        if (registerFirst)
        {
            builder.Services.AddScoped<IReportApiVersions, MarkerReporter>();
            builder.Services.AddApiPipelineVersionDeprecation(builder.Configuration);
        }
        else
        {
            builder.Services.AddApiPipelineVersionDeprecation(builder.Configuration);
            builder.Services.AddScoped<IReportApiVersions, MarkerReporter>();
        }

        await using var app = builder.Build();

        var act = async () => await app.StartAsync();

        await act.Should().NotThrowAsync();
    }

    /// <summary>A reporter registered as a pre-built instance is likewise left alone.</summary>
    [Fact]
    public async Task CustomReportApiVersions_RegisteredAsInstance_IsStillInvoked()
    {
        var builder = CreateBuilder(reportApiVersions: true, deprecationEnabled: true);
        builder.Services.AddSingleton<IReportApiVersions>(new MarkerReporter());
        builder.Services.AddApiPipelineVersionDeprecation(builder.Configuration);

        await using var app = builder.Build();
        var versionSet = app.NewApiVersionSet()
            .HasDeprecatedApiVersion(new ApiVersion(1, 0))
            .Build();
        app.MapGet("/api/v{version:apiVersion}/orders", () => Results.Ok("v1"))
           .WithApiVersionSet(versionSet)
           .MapToApiVersion(new ApiVersion(1, 0));

        await app.StartAsync(TestContext.Current.CancellationToken);
        var response = await app.GetTestClient().GetAsync("/api/v1/orders", TestContext.Current.CancellationToken);

        response.Headers.Contains("X-Marker").Should().BeTrue();
    }

    /// <summary>A reporter that stamps a marker header, standing in for a consumer's own.</summary>
    private sealed class MarkerReporter : IReportApiVersions
    {
        public ApiVersionMapping Mapping => ApiVersionMapping.Explicit | ApiVersionMapping.Implicit;

        public void Report(HttpResponse response, ApiVersionModel apiVersionModel)
            => response.Headers["X-Marker"] = "1";
    }
}

internal static class VersionDeprecationTestBuilderExtensions
{
    public static WebApplication AddApiPipelineVersionDeprecationAndBuild(this WebApplicationBuilder builder)
    {
        builder.Services.AddApiPipelineVersionDeprecation(builder.Configuration);
        return builder.Build();
    }
}
