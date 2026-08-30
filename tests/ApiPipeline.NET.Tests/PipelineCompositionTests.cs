using System.Net;
using ApiPipeline.NET.Correlation;
using FluentAssertions;
using Microsoft.AspNetCore.TestHost;

namespace ApiPipeline.NET.Tests;

/// <summary>
/// Phases run in call order and are never reordered; broken rules are logged, broken security rules
/// fail startup outside Development, and a phase without its registration always fails.
/// </summary>
public sealed class PipelineCompositionTests
{
    private const string WarningPrefix = "ApiPipeline.NET pipeline order:";

    private static async Task<(WebApplication App, CapturingLoggerProvider Logs)> CreateAsync(
        Dictionary<string, string?>? config = null,
        string? environment = null)
    {
        var logs = new CapturingLoggerProvider();
        var app = await TestAppBuilder.CreateAppAsync(
            config ?? TestAppBuilder.MinimalConfig(),
            environment,
            configureServices: s => s.AddSingleton<ILoggerProvider>(logs));
        return (app, logs);
    }

    private static Dictionary<string, string?> OutputCachingOn() =>
        TestAppBuilder.MinimalConfig(c => c["ApiPipeline:OutputCaching:Enabled"] = "true");

    private static string[] OrderWarnings(CapturingLoggerProvider logs)
    {
        lock (logs.Messages)
        {
            return [.. logs.Messages.Where(m => m.StartsWith(WarningPrefix, StringComparison.Ordinal))];
        }
    }

    /// <summary>
    /// Fails if phases are reordered or <c>WithMiddleware</c> is not placed where it is called: the
    /// middleware before <c>WithCorrelationId()</c> must not see the id, the one after must.
    /// </summary>
    [Fact]
    public async Task Phases_RunInCallOrder_AndWithMiddlewareRunsWhereItIsCalled()
    {
        var (app, _) = await CreateAsync();
        await using var appScope = app;
        string? beforeCorrelation = "unset", afterCorrelation = null;

        app.UseApiPipeline(pipeline => pipeline
            .WithMiddleware(a => a.Use(async (ctx, next) =>
            {
                beforeCorrelation = ctx.GetCorrelationId();
                await next(ctx);
            }))
            .WithCorrelationId()
            .WithMiddleware(a => a.Use(async (ctx, next) =>
            {
                afterCorrelation = ctx.GetCorrelationId();
                await next(ctx);
            })));
        app.MapGet("/test", () => "ok");
        await app.StartAsync(TestContext.Current.CancellationToken);

        var response = await app.GetTestClient().GetAsync("/test", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        beforeCorrelation.Should().BeNull("this middleware was added before WithCorrelationId()");
        afterCorrelation.Should().NotBeNullOrEmpty("this middleware was added after WithCorrelationId()");
    }

    /// <summary>An order that breaks a rule is logged, naming both phases and the reason.</summary>
    [Fact]
    public async Task OutOfOrderPhases_LogAWarning_NamingBothPhases()
    {
        var (app, logs) = await CreateAsync();
        await using var appScope = app;

        app.UseApiPipeline(pipeline => pipeline
            .WithResponseCaching()
            .WithAuthentication()
            .WithAuthorization());

        OrderWarnings(logs).Should().ContainSingle(w =>
            w.Contains("Authentication", StringComparison.Ordinal)
            && w.Contains("ResponseCaching", StringComparison.Ordinal))
            .Which.Should().Contain("cached", "the warning says why the order matters");
    }

    /// <summary>
    /// A cache ahead of authorization can replay a response cached for an authorized caller to
    /// anyone. Outside Development that order fails startup, as the package's other security
    /// guards do, instead of leaving a warning to scroll past.
    /// </summary>
    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public async Task SecurityOrderViolation_FailsStartup_OutsideDevelopment(string environment)
    {
        var (app, _) = await CreateAsync(OutputCachingOn(), environment);
        await using var appScope = app;

        var act = () => app.UseApiPipeline(pipeline => pipeline
            .WithOutputCaching()
            .WithAuthentication()
            .WithAuthorization());

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*OutputCaching*Authorization*")
            .Which.Message.Should().Contain("WithMiddleware", "the message names the way to keep a deliberate order");
    }

    /// <summary>In Development the same order is a warning, as every other guard is there.</summary>
    [Fact]
    public async Task SecurityOrderViolation_InDevelopment_OnlyWarns()
    {
        var (app, logs) = await CreateAsync(OutputCachingOn(), Environments.Development);
        await using var appScope = app;

        var act = () => app.UseApiPipeline(pipeline => pipeline
            .WithOutputCaching()
            .WithAuthentication()
            .WithAuthorization());

        act.Should().NotThrow();
        OrderWarnings(logs).Should().Contain(w => w.Contains("OutputCaching", StringComparison.Ordinal));
    }

    /// <summary>
    /// A disabled cache adds no middleware, so its position exposes nothing and must not fail
    /// startup: shared composition code often lists every phase and lets configuration decide.
    /// </summary>
    [Fact]
    public async Task SecurityOrderViolation_WithTheCacheDisabled_DoesNotFail()
    {
        var (app, _) = await CreateAsync(environment: Environments.Production);
        await using var appScope = app;

        var act = () => app.UseApiPipeline(pipeline => pipeline
            .WithOutputCaching()
            .WithAuthentication()
            .WithAuthorization());

        act.Should().NotThrow();
    }

    /// <summary>
    /// Opted in, a security phase whose feature is off fails startup outside Development, naming
    /// each phase and its setting: a Production config that forgot <c>Enabled: true</c> would
    /// otherwise ship without rate limiting.
    /// </summary>
    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public async Task RequireSecurityFeaturesEnabled_DisabledFeature_FailsStartup_OutsideDevelopment(string environment)
    {
        var (app, _) = await CreateAsync(environment: environment);
        await using var appScope = app;

        var act = () => app.UseApiPipeline(pipeline => pipeline
            .RequireSecurityFeaturesEnabled()
            .WithSecurityHeaders()
            .WithAuthentication()
            .WithAuthorization()
            .WithRateLimiting());

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().ContainAll(
                "WithSecurityHeaders()", "WithRateLimiting()", "ApiPipeline:SecurityHeaders:Enabled", "ApiPipeline:RateLimiting:Enabled");
    }

    /// <summary>In Development the opt-in changes nothing: event 15 is logged, startup continues.</summary>
    [Fact]
    public async Task RequireSecurityFeaturesEnabled_InDevelopment_OnlyWarns()
    {
        var (app, logs) = await CreateAsync(environment: Environments.Development);
        await using var appScope = app;

        var act = () => app.UseApiPipeline(pipeline => pipeline
            .RequireSecurityFeaturesEnabled()
            .WithRateLimiting());

        act.Should().NotThrow();
        lock (logs.Messages)
        {
            logs.Messages.Should().Contain(m => m.Contains("WithRateLimiting()", StringComparison.Ordinal));
        }
    }

    /// <summary>Not opted in, a disabled security feature stays the warning it always was.</summary>
    [Fact]
    public async Task DisabledSecurityFeature_WithoutTheOptIn_DoesNotFail()
    {
        var (app, _) = await CreateAsync(environment: Environments.Production);
        await using var appScope = app;

        var act = () => app.UseApiPipeline(pipeline => pipeline.WithRateLimiting());

        act.Should().NotThrow();
    }

    /// <summary>
    /// Configuration outside the <c>ApiPipeline</c> section leaves rate limiting and
    /// timeouts on their defaults (off). Opted in, that fails startup in Production instead of
    /// shipping without them.
    /// </summary>
    [Fact]
    public async Task RequireSecurityFeaturesEnabled_UnmigratedConfiguration_FailsStartup()
    {
        var config = TestAppBuilder.MinimalConfig(c =>
        {
            c.Remove("ApiPipeline:RateLimiting:Enabled");
            c["RateLimitingOptions:Enabled"] = "true";
            c["RateLimitingOptions:DefaultPolicy"] = "strict";
            c["RateLimitingOptions:Policies:0:Name"] = "strict";
            c["RateLimitingOptions:Policies:0:PermitLimit"] = "100";
            c["RateLimitingOptions:Policies:0:WindowSeconds"] = "60";
            c["RequestTimeoutsOptions:Enabled"] = "true";
        });
        var (app, _) = await CreateAsync(config, Environments.Production);
        await using var appScope = app;

        var act = () => app.UseApiPipeline(pipeline => pipeline
            .RequireSecurityFeaturesEnabled()
            .WithRequestTimeouts()
            .WithAuthentication()
            .WithAuthorization()
            .WithRateLimiting());

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().ContainAll(
                "WithRequestTimeouts()", "WithRateLimiting()", "ApiPipeline:RequestTimeouts:Enabled", "ApiPipeline:RateLimiting:Enabled");
    }

    /// <summary>
    /// Only fail-open features count. CORS off blocks cross-origin calls, and the response
    /// features weaken nothing, so neither fails startup.
    /// </summary>
    [Fact]
    public async Task RequireSecurityFeaturesEnabled_IgnoresFeaturesThatFailSafe()
    {
        var (app, _) = await CreateAsync(environment: Environments.Production);
        await using var appScope = app;

        var act = () => app.UseApiPipeline(pipeline => pipeline
            .RequireSecurityFeaturesEnabled()
            .WithCors()
            .WithResponseCompression()
            .WithOutputCaching());

        act.Should().NotThrow();
    }

    /// <summary>Enabled security features pass.</summary>
    [Fact]
    public async Task RequireSecurityFeaturesEnabled_WithTheFeatureEnabled_DoesNotFail()
    {
        var (app, _) = await CreateAsync(
            TestAppBuilder.MinimalConfig(c => c["ApiPipeline:SecurityHeaders:Enabled"] = "true"),
            Environments.Production);
        await using var appScope = app;

        var act = () => app.UseApiPipeline(pipeline => pipeline
            .RequireSecurityFeaturesEnabled()
            .WithSecurityHeaders());

        act.Should().NotThrow();
    }

    /// <summary>
    /// A rule that only costs a header on short-circuited responses stays a warning everywhere.
    /// </summary>
    [Fact]
    public async Task NonSecurityOrderViolation_OutsideDevelopment_OnlyWarns()
    {
        var (app, logs) = await CreateAsync(environment: Environments.Production);
        await using var appScope = app;

        var act = () => app.UseApiPipeline(pipeline => pipeline
            .WithAuthentication()
            .WithCorrelationId());

        act.Should().NotThrow();
        OrderWarnings(logs).Should().ContainSingle();
    }

    /// <summary>
    /// Microsoft's documented order puts CORS before the caches: a cache hit short-circuits, so a
    /// response served from cache ahead of CORS carries no CORS headers and the browser blocks it.
    /// </summary>
    [Theory]
    [InlineData("OutputCaching")]
    [InlineData("ResponseCaching")]
    public async Task CorsAfterACache_LogsAWarning(string cache)
    {
        var (app, logs) = await CreateAsync();
        await using var appScope = app;

        app.UseApiPipeline(pipeline =>
        {
            _ = cache == "OutputCaching" ? pipeline.WithOutputCaching() : pipeline.WithResponseCaching();
            pipeline.WithCors();
        });

        OrderWarnings(logs).Should().Contain(w =>
            w.Contains($"{cache} runs before Cors", StringComparison.Ordinal));
    }

    /// <summary>
    /// A feature phase whose <c>AddApiPipeline*</c> registration is missing would read unbound
    /// options and add nothing, so <c>ApiPipeline:RateLimiting:Enabled=true</c> would be silently
    /// ignored. It fails instead, as <c>UseAuthorization()</c> without <c>AddAuthorization()</c> does.
    /// </summary>
    [Fact]
    public async Task FeaturePhase_WithoutItsRegistration_Throws()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Development });
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ApiPipeline:RateLimiting:Enabled"] = "true",
        });
        builder.Services.AddApiPipelineCorrelationId();
        await using var app = builder.Build();

        var act = () => app.UseApiPipeline(pipeline => pipeline
            .WithCorrelationId()
            .WithRateLimiting());

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*WithRateLimiting()*AddApiPipelineRateLimiting*");
    }

    /// <summary>The order is not corrected: an out-of-order pipeline runs as written.</summary>
    [Fact]
    public async Task OutOfOrderPhases_AreNotReordered()
    {
        var (app, _) = await CreateAsync();
        await using var appScope = app;
        string? seenByMiddleware = "unset";

        // Correlation after the custom middleware: the custom middleware must not see an id.
        app.UseApiPipeline(pipeline => pipeline
            .WithMiddleware(a => a.Use(async (ctx, next) =>
            {
                seenByMiddleware = ctx.GetCorrelationId();
                await next(ctx);
            }))
            .WithAuthentication()
            .WithCorrelationId());
        app.MapGet("/test", () => "ok");
        await app.StartAsync(TestContext.Current.CancellationToken);

        await app.GetTestClient().GetAsync("/test", TestContext.Current.CancellationToken);

        seenByMiddleware.Should().BeNull();
    }

    /// <summary>The recommended order, the one the README shows, logs no ordering warning.</summary>
    [Fact]
    public async Task RecommendedOrder_LogsNoWarning()
    {
        var (app, logs) = await CreateAsync();
        await using var appScope = app;

        app.UseApiPipeline(pipeline => pipeline
            .WithForwardedHeaders()
            .WithRequestSizeTracking()
            .WithCorrelationId()
            .WithSecurityHeaders()
            .WithRequestTimeouts()
            .WithHttpsRedirection()
            .WithCors()
            .WithEdgeRateLimiting()
            .WithAuthentication()
            .WithAuthorization()
            .WithMiddleware(_ => { })
            .WithRateLimiting()
            .WithResponseCompression()
            .WithOutputCaching());

        OrderWarnings(logs).Should().BeEmpty();
    }

    /// <summary>
    /// A phase requested twice is applied once, at its first position, with a warning. Applied twice,
    /// the second correlation middleware would overwrite the id the endpoint sees while the first
    /// one's response header won, so the two would differ.
    /// </summary>
    [Fact]
    public async Task DuplicatePhase_IsAppliedOnce_AndLogsAWarning()
    {
        var (app, logs) = await CreateAsync();
        await using var appScope = app;

        app.UseApiPipeline(pipeline => pipeline
            .WithCorrelationId()
            .WithMiddleware(_ => { })
            .WithCorrelationId());
        app.MapGet("/test", (HttpContext ctx) => ctx.GetCorrelationId());
        await app.StartAsync(TestContext.Current.CancellationToken);

        var response = await app.GetTestClient().GetAsync("/test", TestContext.Current.CancellationToken);
        var idSeenByEndpoint = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        OrderWarnings(logs).Should().ContainSingle(w => w.Contains("CorrelationId", StringComparison.Ordinal)
            && w.Contains("more than once", StringComparison.Ordinal));
        response.Headers.GetValues(CorrelationHeaders.CorrelationId).Should().ContainSingle()
            .Which.Should().Be(idSeenByEndpoint);
    }

    /// <summary>
    /// <c>WithRouting()</c> places routing explicitly; after an endpoint-aware phase it is warned
    /// about, because that phase then cannot see the endpoint.
    /// </summary>
    [Fact]
    public async Task ExplicitRouting_AfterAnEndpointAwarePhase_LogsAWarning()
    {
        var (app, logs) = await CreateAsync();
        await using var appScope = app;

        app.UseApiPipeline(pipeline => pipeline
            .WithCors()
            .WithRouting());

        OrderWarnings(logs).Should().ContainSingle(w =>
            w.Contains("Routing", StringComparison.Ordinal) && w.Contains("Cors", StringComparison.Ordinal));
    }

    /// <summary>Without <c>WithRouting()</c>, routing is still added automatically, after forwarded headers.</summary>
    [Fact]
    public async Task ImplicitRouting_IsAddedAfterForwardedHeaders_WithoutWarnings()
    {
        var (app, logs) = await CreateAsync();
        await using var appScope = app;
        var sawEndpoint = false;

        app.UseApiPipeline(pipeline => pipeline
            .WithMiddleware(a => a.Use(async (ctx, next) =>
            {
                sawEndpoint = ctx.GetEndpoint() is not null;
                await next(ctx);
            }))
            .WithForwardedHeaders()
            .WithCors());
        app.MapGet("/test", () => "ok");
        await app.StartAsync(TestContext.Current.CancellationToken);

        await app.GetTestClient().GetAsync("/test", TestContext.Current.CancellationToken);

        OrderWarnings(logs).Should().BeEmpty();
        sawEndpoint.Should().BeFalse("routing is added after forwarded headers, so the first middleware runs before it");
    }

    /// <summary><c>WithMiddleware</c> rejects a null delegate.</summary>
    [Fact]
    public async Task WithMiddleware_Null_Throws()
    {
        var (app, _) = await CreateAsync();
        await using var appScope = app;

        var act = () => app.UseApiPipeline(pipeline => pipeline.WithMiddleware(null!));

        act.Should().Throw<ArgumentNullException>();
    }
}
