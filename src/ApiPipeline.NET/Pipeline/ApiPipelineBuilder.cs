using ApiPipeline.NET.Configuration;
using ApiPipeline.NET.Observability;
using Microsoft.Extensions.Hosting;

namespace ApiPipeline.NET.Pipeline;

/// <summary>Builds the middleware pipeline for <c>UseApiPipeline</c>.</summary>
/// <remarks>
/// <list type="bullet">
/// <item>Phases run in the order the <c>With*</c> methods are called; nothing is reordered.</item>
/// <item>An order that breaks a rule is logged; a broken security rule also fails startup outside
/// Development. <see cref="WithMiddleware"/> steps are not checked.</item>
/// <item>A phase whose <c>AddApiPipeline*</c> registration is missing fails the build.</item>
/// <item>Routing is added after <see cref="WithForwardedHeaders"/> (or first) unless
/// <see cref="WithRouting"/> places it.</item>
/// <item>Settings are read once at startup; a disabled phase adds nothing and logs event 15.
/// <see cref="RequireSecurityFeaturesEnabled"/> makes that a startup failure outside Development
/// for the security phases.</item>
/// </list>
/// </remarks>
public sealed class ApiPipelineBuilder
{
    internal const string LoggerCategory = "ApiPipeline.NET.Pipeline";

    /// <summary>Phases that need an <c>AddApiPipeline*</c> registration.</summary>
    private static readonly PipelinePhase[] RegisteredFeaturePhases =
    [
        PipelinePhase.ForwardedHeaders, PipelinePhase.SecurityHeaders, PipelinePhase.RequestTimeouts,
        PipelinePhase.Cors, PipelinePhase.EdgeRateLimiting, PipelinePhase.RateLimiting,
        PipelinePhase.ResponseCompression, PipelinePhase.ResponseCaching, PipelinePhase.OutputCaching,
    ];

    /// <summary>
    /// Phases whose feature, when off, weakens protection: limits, timeouts, headers and the real
    /// client address. CORS is not one: off, it blocks cross-origin calls, which fails safe.
    /// </summary>
    internal static readonly PipelinePhase[] SecurityFeaturePhases =
    [
        PipelinePhase.ForwardedHeaders, PipelinePhase.SecurityHeaders, PipelinePhase.RequestTimeouts,
        PipelinePhase.EdgeRateLimiting, PipelinePhase.RateLimiting,
    ];

    private readonly List<Step> _steps = [];
    private readonly List<PipelinePhase> _duplicates = [];
    private bool _requireSecurityFeaturesEnabled;

    internal ApiPipelineBuilder()
    {
    }

    /// <summary>Adds forwarded headers processing. Call it first.</summary>
    public ApiPipelineBuilder WithForwardedHeaders() => With(PipelinePhase.ForwardedHeaders);

    /// <summary>
    /// Places routing explicitly, before every phase that reads endpoint metadata. Optional.
    /// </summary>
    public ApiPipelineBuilder WithRouting() => With(PipelinePhase.Routing);

    /// <summary>Adds request body size telemetry.</summary>
    public ApiPipelineBuilder WithRequestSizeTracking() => With(PipelinePhase.RequestSizeTracking);

    /// <summary>Adds correlation ids. Call it before any phase that can short-circuit.</summary>
    public ApiPipelineBuilder WithCorrelationId() => With(PipelinePhase.CorrelationId);

    /// <summary>Adds security headers and HSTS. Call it before any phase that can short-circuit.</summary>
    public ApiPipelineBuilder WithSecurityHeaders() => With(PipelinePhase.SecurityHeaders);

    /// <summary>Adds request timeouts. Recommended after the header phases, before authentication.</summary>
    public ApiPipelineBuilder WithRequestTimeouts() => With(PipelinePhase.RequestTimeouts);

    /// <summary>Adds HTTPS redirection.</summary>
    public ApiPipelineBuilder WithHttpsRedirection() => With(PipelinePhase.HttpsRedirection);

    /// <summary>Adds CORS. Recommended before authentication and rate limiting.</summary>
    public ApiPipelineBuilder WithCors() => With(PipelinePhase.Cors);

    /// <summary>Adds pre-authentication, IP-partitioned rate limiting.</summary>
    public ApiPipelineBuilder WithEdgeRateLimiting() => With(PipelinePhase.EdgeRateLimiting);

    /// <summary>Adds authentication.</summary>
    public ApiPipelineBuilder WithAuthentication() => With(PipelinePhase.Authentication);

    /// <summary>Adds authorization.</summary>
    public ApiPipelineBuilder WithAuthorization() => With(PipelinePhase.Authorization);

    /// <summary>Adds identity-partitioned rate limiting. Recommended after authentication.</summary>
    public ApiPipelineBuilder WithRateLimiting() => With(PipelinePhase.RateLimiting);

    /// <summary>Adds response compression.</summary>
    public ApiPipelineBuilder WithResponseCompression() => With(PipelinePhase.ResponseCompression);

    /// <summary>Adds response caching. Obsolete: use <see cref="WithOutputCaching"/>.</summary>
    [Obsolete(ApiPipelineDiagnostics.ResponseCachingObsoleteMessage, DiagnosticId = ApiPipelineDiagnostics.ResponseCachingObsoleteId, UrlFormat = ApiPipelineDiagnostics.UrlFormat)]
    public ApiPipelineBuilder WithResponseCaching() => With(PipelinePhase.ResponseCaching);

    /// <summary>Adds output caching.</summary>
    public ApiPipelineBuilder WithOutputCaching() => With(PipelinePhase.OutputCaching);

    /// <summary>Adds the application's own middleware here. Not checked against the ordering rules.</summary>
    /// <param name="configure">Adds the middleware.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <example>
    /// <code>
    /// app.UseApiPipeline(pipeline => pipeline
    ///     .WithForwardedHeaders()
    ///     .WithAuthentication()
    ///     .WithMiddleware(a => a.UseMiddleware&lt;TenantResolutionMiddleware&gt;())
    ///     .WithAuthorization()
    ///     .WithRateLimiting());
    /// </code>
    /// </example>
    public ApiPipelineBuilder WithMiddleware(Action<IApplicationBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        _steps.Add(new Step(null, configure));
        return this;
    }

    /// <summary>
    /// Fails startup outside Development when a security phase in this pipeline has its feature
    /// disabled, instead of logging event 15. Opt-in; where it is called in the chain does not matter.
    /// </summary>
    /// <remarks>
    /// Covers <see cref="WithForwardedHeaders"/>, <see cref="WithSecurityHeaders"/>,
    /// <see cref="WithRequestTimeouts"/>, <see cref="WithEdgeRateLimiting"/> and
    /// <see cref="WithRateLimiting"/>: a configuration that sets one of their <c>Enabled</c> to
    /// <c>false</c>, or never sets the ones off by default, would otherwise ship without that
    /// protection. To run without a feature on purpose, remove its <c>With*()</c> call.
    /// </remarks>
    /// <returns>The same builder, for chaining.</returns>
    /// <example>
    /// <code>
    /// app.UseApiPipeline(pipeline => pipeline
    ///     .RequireSecurityFeaturesEnabled()
    ///     .WithForwardedHeaders()
    ///     .WithSecurityHeaders()
    ///     .WithAuthentication()
    ///     .WithAuthorization()
    ///     .WithRateLimiting());
    /// </code>
    /// </example>
    public ApiPipelineBuilder RequireSecurityFeaturesEnabled()
    {
        _requireSecurityFeaturesEnabled = true;
        return this;
    }

    private ApiPipelineBuilder With(PipelinePhase phase)
    {
        // Applied once, at the first position; the repeat is reported.
        if (_steps.Exists(step => step.Phase == phase))
        {
            _duplicates.Add(phase);
        }
        else
        {
            _steps.Add(new Step(phase, null));
        }

        return this;
    }

    internal void Build(IApplicationBuilder app)
    {
        var steps = WithImplicitRouting(_steps);
        var features = app.ApplicationServices.GetServices<PipelineFeature>().ToList();

        EnsureRegistered(steps, features);

        app.ApplicationServices.GetService<PipelineWiring>()?.Record(
            steps.Where(step => step.Phase is not null).Select(step => step.Phase!.Value));

        var logger = app.ApplicationServices.GetService<ILoggerFactory>()?.CreateLogger(LoggerCategory);

        var custom = 0;
        var names = steps.ConvertAll(step => step.Phase?.ToString() ?? $"Middleware#{++custom}");

        logger?.PipelineComposed(string.Join(" -> ", names));

        ReportOrder(logger, steps);
        ReportDisabledPhases(logger, app.ApplicationServices, features, steps);
        FailOnUnsafeOrder(app.ApplicationServices, features, steps);
        if (_requireSecurityFeaturesEnabled)
        {
            FailOnDisabledSecurityFeatures(app.ApplicationServices, features, steps);
        }

        foreach (var step in steps)
        {
            if (step.Custom is { } configure)
            {
                configure(app);
                continue;
            }

            switch (step.Phase)
            {
                case PipelinePhase.ForwardedHeaders:    app.UseApiPipelineForwardedHeaders(); break;
                case PipelinePhase.Routing:             app.UseRouting(); break;
                case PipelinePhase.RequestSizeTracking: app.UseApiPipelineRequestSizeTracking(); break;
                case PipelinePhase.CorrelationId:       app.UseApiPipelineCorrelationId(); break;
                case PipelinePhase.SecurityHeaders:     app.UseApiPipelineSecurityHeaders(); break;
                case PipelinePhase.RequestTimeouts:     app.UseApiPipelineRequestTimeouts(); break;
                case PipelinePhase.HttpsRedirection:    app.UseHttpsRedirection(); break;
                case PipelinePhase.Cors:                app.UseApiPipelineCors(); break;
                case PipelinePhase.EdgeRateLimiting:    app.UseApiPipelineEdgeRateLimiting(); break;
                case PipelinePhase.Authentication:      app.UseAuthentication(); break;
                case PipelinePhase.Authorization:       app.UseAuthorization(); break;
                case PipelinePhase.RateLimiting:        app.UseApiPipelineRateLimiting(); break;
                case PipelinePhase.ResponseCompression: app.UseApiPipelineResponseCompression(); break;
                case PipelinePhase.ResponseCaching:     app.UseApiPipelineResponseCaching(); break;
                case PipelinePhase.OutputCaching:       app.UseApiPipelineOutputCaching(); break;
            }
        }
    }

    /// <summary>Adds routing after forwarded headers, or first, unless placed.</summary>
    private static List<Step> WithImplicitRouting(List<Step> steps)
    {
        var result = new List<Step>(steps);
        if (result.Exists(step => step.Phase == PipelinePhase.Routing))
        {
            return result;
        }

        var forwarded = result.FindIndex(step => step.Phase == PipelinePhase.ForwardedHeaders);
        result.Insert(forwarded + 1, new Step(PipelinePhase.Routing, null));
        return result;
    }

    private static void EnsureRegistered(List<Step> steps, List<PipelineFeature> features)
    {
        var missing = steps
            .Where(step => step.Phase is { } phase
                && Array.IndexOf(RegisteredFeaturePhases, phase) >= 0
                && !features.Exists(feature => feature.Phase == phase))
            .Select(step => step.Phase!.Value)
            .Distinct()
            .ToList();

        if (missing.Count == 0)
        {
            return;
        }

        throw new InvalidOperationException(
            "ApiPipeline.NET: " + string.Join(", ", missing.Select(phase => $"With{phase}()")) +
            " was called, but the feature's services are not registered, so it would read default " +
            "options and add no middleware. Call " +
            string.Join(", ", missing.Select(phase => $"services.AddApiPipeline{phase}(configuration)")) +
            " in the application startup code, or services.AddApiPipeline(configuration), which " +
            "registers every feature.");
    }

    /// <summary>Fails startup outside Development for a broken security rule whose phases both add middleware.</summary>
    private static void FailOnUnsafeOrder(
        IServiceProvider services,
        List<PipelineFeature> features,
        List<Step> steps)
    {
        var environment = services.GetService<IHostEnvironment>();

        // No environment: fail closed.
        if (environment is not null && !StrictEnvironment.Applies(environment))
        {
            return;
        }

        bool AddsMiddleware(PipelinePhase phase) =>
            features.Find(feature => feature.Phase == phase) is not { } feature || feature.IsEnabled(services);

        var order = steps.Where(step => step.Phase is not null).Select(step => step.Phase!.Value).ToList();
        var unsafeRules = PipelineOrderRules.Violations(order)
            .Where(rule => rule.Security && AddsMiddleware(rule.First) && AddsMiddleware(rule.Then))
            .ToList();

        if (unsafeRules.Count == 0)
        {
            return;
        }

        var described = environment is null ? "unknown" : StrictEnvironment.Describe(environment);
        throw new InvalidOperationException(
            "ApiPipeline.NET pipeline order is unsafe: " +
            string.Join("; ", unsafeRules.Select(rule =>
                $"{rule.Then} runs before {rule.First}, but {rule.First} should run first: {rule.Reason}")) +
            ". Reorder the With...() calls. If the order is deliberate, add that middleware yourself " +
            "with WithMiddleware(...) at the position you need; WithMiddleware steps are not checked. " +
            $"(Environment {described}: this check fails startup in every environment except Development.)");
    }

    /// <summary>
    /// For <see cref="RequireSecurityFeaturesEnabled"/>: fails outside Development when a requested
    /// security phase's feature is off.
    /// </summary>
    private static void FailOnDisabledSecurityFeatures(
        IServiceProvider services,
        List<PipelineFeature> features,
        List<Step> steps)
    {
        var environment = services.GetService<IHostEnvironment>();

        // No environment: fail closed, as the order check does.
        if (environment is not null && !StrictEnvironment.Applies(environment))
        {
            return;
        }

        var disabled = steps
            .Where(step => step.Phase is { } phase && Array.IndexOf(SecurityFeaturePhases, phase) >= 0)
            .Select(step => features.Find(feature => feature.Phase == step.Phase))
            .Where(feature => feature is not null && !feature.IsEnabled(services))
            .Select(feature => feature!)
            .DistinctBy(feature => feature.Phase)
            .ToList();

        if (disabled.Count == 0)
        {
            return;
        }

        var described = environment is null ? "unknown" : StrictEnvironment.Describe(environment);
        throw new InvalidOperationException(
            "ApiPipeline.NET: RequireSecurityFeaturesEnabled() is set, but " +
            string.Join(", ", disabled.Select(feature => $"With{feature.Phase}()")) +
            " is in the pipeline while its feature is disabled (" +
            string.Join(", ", disabled.Select(feature => $"{feature.ConfigurationKey}:Enabled")) +
            " is false), so the API would run without that protection. Set Enabled to true in the " +
            "configuration for this environment, or remove the With...() call to run without the " +
            $"feature on purpose. (Environment {described}: this check fails startup in every " +
            "environment except Development.)");
    }

    private void ReportOrder(ILogger? logger, List<Step> steps)
    {
        if (logger is null)
        {
            return;
        }

        foreach (var phase in _duplicates.Distinct())
        {
            logger.PhaseRequestedTwice(phase);
        }

        var order = steps.Where(step => step.Phase is not null).Select(step => step.Phase!.Value).ToList();
        foreach (var rule in PipelineOrderRules.Violations(order))
        {
            logger.PhaseOrderViolated(rule.Then, rule.First, rule.Reason);
        }
    }

    /// <summary>Warns once per requested phase whose feature is off.</summary>
    private static void ReportDisabledPhases(
        ILogger? logger,
        IServiceProvider services,
        List<PipelineFeature> features,
        List<Step> steps)
    {
        if (logger is null)
        {
            return;
        }

        foreach (var phase in steps.Where(step => step.Phase is not null).Select(step => step.Phase!.Value).Distinct())
        {
            if (features.Find(feature => feature.Phase == phase) is { } feature && !feature.IsEnabled(services))
            {
                logger.PhaseFeatureDisabled(phase, feature.ConfigurationKey);
            }
        }
    }

    private sealed record Step(PipelinePhase? Phase, Action<IApplicationBuilder>? Custom);
}
