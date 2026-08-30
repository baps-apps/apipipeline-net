using FluentAssertions;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging;

namespace ApiPipeline.NET.Tests;

/// <summary>
/// A feature switched on in configuration does nothing until its phase is in the pipeline. Enabling
/// rate limiting and forgetting <c>WithRateLimiting()</c> — or forgetting <c>UseApiPipeline</c>
/// altogether — must not leave an API silently unprotected, so each case is reported at startup.
/// </summary>
public sealed class UnwiredFeatureWarningTests
{
    private const string NotInPipeline = "never added to the pipeline";
    private const string NeverCalled = "UseApiPipeline(...) was never called";

    private static async Task<List<string>> StartAndCaptureAsync(
        Dictionary<string, string?> config,
        Action<WebApplication> configurePipeline)
    {
        // Production, so warnings are not development-only; a trust set keeps the forwarded headers
        // guard from failing startup.
        config["ApiPipeline:ForwardedHeaders:KnownNetworks:0"] = "10.0.0.0/8";

        var capture = new CapturingLoggerProvider();
        await using var app = await TestAppBuilder.CreateAppAsync(
            config,
            "Production",
            configureServices: services => services.AddSingleton<ILoggerProvider>(capture));
        configurePipeline(app);
        app.MapGet("/test", () => "ok");
        await app.StartAsync(TestContext.Current.CancellationToken);
        return capture.Messages;
    }

    /// <summary>Rate limiting on in configuration, its phase missing: reported by name.</summary>
    [Fact]
    public async Task EnabledFeature_PhaseNotRequested_Warns()
    {
        var messages = await StartAndCaptureAsync(
            TestAppBuilder.WithRateLimiting(),
            app => app.UseApiPipeline(p => p.WithForwardedHeaders().WithCorrelationId()));

        messages.Should().Contain(m =>
            m.Contains(NotInPipeline, StringComparison.Ordinal)
            && m.Contains("ApiPipeline:RateLimiting:Enabled", StringComparison.Ordinal)
            && m.Contains("WithRateLimiting()", StringComparison.Ordinal));
    }

    /// <summary>The correct configuration produces no warning.</summary>
    [Fact]
    public async Task EnabledFeature_PhaseRequested_DoesNotWarn()
    {
        var messages = await StartAndCaptureAsync(
            TestAppBuilder.WithRateLimiting(),
            app => app.UseApiPipeline(p => p.WithForwardedHeaders().WithRateLimiting()));

        messages.Should().NotContain(m =>
            m.Contains(NotInPipeline, StringComparison.Ordinal) || m.Contains(NeverCalled, StringComparison.Ordinal));
    }

    /// <summary>A feature that is off needs no phase.</summary>
    [Fact]
    public async Task DisabledFeature_PhaseNotRequested_DoesNotWarn()
    {
        var messages = await StartAndCaptureAsync(
            TestAppBuilder.MinimalConfig(),
            app => app.UseApiPipeline(p => p.WithForwardedHeaders()));

        messages.Should().NotContain(m =>
            m.Contains(NotInPipeline, StringComparison.Ordinal) || m.Contains(NeverCalled, StringComparison.Ordinal));
    }

    /// <summary>Each enabled feature is reported on its own, so none hides behind another.</summary>
    [Fact]
    public async Task SeveralEnabledFeatures_PhasesNotRequested_WarnsForEach()
    {
        var messages = await StartAndCaptureAsync(
            TestAppBuilder.MinimalConfig(c =>
            {
                c["ApiPipeline:SecurityHeaders:Enabled"] = "true";
                c["ApiPipeline:Cors:Enabled"] = "true";
                c["ApiPipeline:Cors:AllowedOrigins:0"] = "https://app.example.com";
            }),
            app => app.UseApiPipeline(p => p.WithForwardedHeaders()));

        messages.Should().Contain(m => m.Contains(NotInPipeline, StringComparison.Ordinal)
            && m.Contains("WithSecurityHeaders()", StringComparison.Ordinal));
        messages.Should().Contain(m => m.Contains(NotInPipeline, StringComparison.Ordinal)
            && m.Contains("WithCors()", StringComparison.Ordinal));
    }

    /// <summary>A phase requested in any <c>UseApiPipeline</c> call (a branch, say) counts.</summary>
    [Fact]
    public async Task PhaseRequestedInSecondUseApiPipelineCall_DoesNotWarn()
    {
        var messages = await StartAndCaptureAsync(
            TestAppBuilder.WithRateLimiting(),
            app =>
            {
                app.UseApiPipeline(p => p.WithForwardedHeaders());
                app.UseApiPipeline(p => p.WithRateLimiting());
            });

        messages.Should().NotContain(m => m.Contains(NotInPipeline, StringComparison.Ordinal));
    }

    /// <summary>No <c>UseApiPipeline</c> at all: one warning that says so, naming the features.</summary>
    [Fact]
    public async Task EnabledFeature_UseApiPipelineNeverCalled_Warns()
    {
        var messages = await StartAndCaptureAsync(TestAppBuilder.WithRateLimiting(), _ => { });

        messages.Should().Contain(m =>
            m.Contains(NeverCalled, StringComparison.Ordinal)
            && m.Contains("ApiPipeline:RateLimiting", StringComparison.Ordinal));
        messages.Should().NotContain(m => m.Contains(NotInPipeline, StringComparison.Ordinal));
    }

    /// <summary>
    /// Only features the application registered are checked: security headers default to enabled,
    /// but an application that only added rate limiting never asked for them.
    /// </summary>
    [Fact]
    public async Task FeatureNeverRegistered_IsNotReported()
    {
        var capture = new CapturingLoggerProvider();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Production,
        });
        builder.WebHost.UseTestServer();
        builder.Logging.AddProvider(capture);
        builder.Configuration.AddInMemoryCollection(TestAppBuilder.WithRateLimiting());
        builder.Services.AddApiPipelineRateLimiting(builder.Configuration);

        await using var app = builder.Build();
        app.UseApiPipeline(p => p.WithRateLimiting());
        app.MapGet("/test", () => "ok");
        await app.StartAsync(TestContext.Current.CancellationToken);

        capture.Messages.Should().NotContain(m =>
            m.Contains(NotInPipeline, StringComparison.Ordinal) || m.Contains(NeverCalled, StringComparison.Ordinal));
    }

    private const string WiredButDisabled = "is in the pipeline, but";

    /// <summary>
    /// The inverse mistake: <c>WithCors()</c> called while <c>ApiPipeline:Cors:Enabled</c> is false
    /// (its default). The phase adds no middleware, so it is reported with both ways out.
    /// </summary>
    [Fact]
    public async Task PhaseRequested_FeatureDisabled_Warns()
    {
        var messages = await StartAndCaptureAsync(
            TestAppBuilder.MinimalConfig(),
            app => app.UseApiPipeline(p => p.WithForwardedHeaders().WithCors()));

        messages.Should().ContainSingle(m => m.Contains(WiredButDisabled, StringComparison.Ordinal))
            .Which.Should().Contain("WithCors()").And.Contain("ApiPipeline:Cors:Enabled is false");
    }

    /// <summary>The same phase with its feature on adds middleware: no warning.</summary>
    [Fact]
    public async Task PhaseRequested_FeatureEnabled_DoesNotWarn()
    {
        var messages = await StartAndCaptureAsync(
            TestAppBuilder.MinimalConfig(c =>
            {
                c["ApiPipeline:Cors:Enabled"] = "true";
                c["ApiPipeline:Cors:AllowedOrigins:0"] = "https://app.example.com";
            }),
            app => app.UseApiPipeline(p => p.WithForwardedHeaders().WithCors()));

        messages.Should().NotContain(m => m.Contains(WiredButDisabled, StringComparison.Ordinal));
    }

    /// <summary>Each disabled phase is reported on its own, once, however often it was requested.</summary>
    [Fact]
    public async Task SeveralDisabledPhases_WarnsOncePerPhase()
    {
        var messages = await StartAndCaptureAsync(
            TestAppBuilder.MinimalConfig(),
            app => app.UseApiPipeline(p => p
                .WithForwardedHeaders()
                .WithRequestTimeouts()
                .WithRateLimiting()
                .WithRateLimiting()
                .WithOutputCaching()));

        var warnings = messages.Where(m => m.Contains(WiredButDisabled, StringComparison.Ordinal)).ToList();
        warnings.Should().HaveCount(3);
        warnings.Should().ContainSingle(m => m.Contains("WithRequestTimeouts()", StringComparison.Ordinal));
        warnings.Should().ContainSingle(m => m.Contains("WithRateLimiting()", StringComparison.Ordinal));
        warnings.Should().ContainSingle(m => m.Contains("WithOutputCaching()", StringComparison.Ordinal));
    }

    /// <summary>
    /// Phases with no <c>Enabled</c> flag (correlation id, routing, authentication) and phases whose
    /// feature is on by default (forwarded headers, security headers) are never reported.
    /// </summary>
    [Fact]
    public async Task PhasesWithoutAFlagOrOnByDefault_DoNotWarn()
    {
        var messages = await StartAndCaptureAsync(
            // Security headers left at their default (on).
            TestAppBuilder.MinimalConfig(c => c.Remove("ApiPipeline:SecurityHeaders:Enabled")),
            app => app.UseApiPipeline(p => p
                .WithForwardedHeaders()
                .WithRouting()
                .WithCorrelationId()
                .WithSecurityHeaders()
                .WithRequestSizeTracking()));

        messages.Should().NotContain(m => m.Contains(WiredButDisabled, StringComparison.Ordinal));
    }

    /// <summary>
    /// The startup checks run in a container without a host (a worker, a test or benchmark harness);
    /// the check that needs <c>IHostApplicationLifetime</c> skips itself instead of throwing.
    /// </summary>
    [Fact]
    public async Task WithoutAHostLifetime_StartupChecksStillRun()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddApiPipelineRateLimiting(new ConfigurationBuilder().AddInMemoryCollection(
            TestAppBuilder.WithRateLimiting()).Build());
        await using var provider = services.BuildServiceProvider();

        var start = async () =>
        {
            // As the host does: every StartingAsync (where the checks run), then every StartAsync.
            var hostedServices = provider.GetServices<IHostedService>().ToList();
            hostedServices.OfType<IHostedLifecycleService>().Should().NotBeEmpty();
            foreach (var lifecycle in hostedServices.OfType<IHostedLifecycleService>())
            {
                await lifecycle.StartingAsync(TestContext.Current.CancellationToken);
            }

            foreach (var hosted in hostedServices)
            {
                await hosted.StartAsync(TestContext.Current.CancellationToken);
            }
        };

        await start.Should().NotThrowAsync();
    }
}
