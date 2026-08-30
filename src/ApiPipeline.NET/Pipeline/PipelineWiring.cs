using System.Diagnostics.CodeAnalysis;
using ApiPipeline.NET.Configuration;
using ApiPipeline.NET.Observability;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace ApiPipeline.NET.Pipeline;

/// <summary>A registered feature whose middleware comes from a phase.</summary>
/// <param name="Phase">The phase.</param>
/// <param name="ConfigurationKey">The feature's full section path, for example <c>ApiPipeline:RateLimiting</c>.</param>
/// <param name="IsEnabled">Whether the validated options turn it on.</param>
internal sealed record PipelineFeature(
    PipelinePhase Phase,
    string ConfigurationKey,
    Func<IServiceProvider, bool> IsEnabled);

/// <summary>The phases every <c>UseApiPipeline</c> call requested.</summary>
internal sealed class PipelineWiring
{
    private readonly Lock _gate = new();
    private readonly HashSet<PipelinePhase> _requested = [];
    private bool _used;

    public void Record(IEnumerable<PipelinePhase> phases)
    {
        lock (_gate)
        {
            _used = true;
            _requested.UnionWith(phases);
        }
    }

    public bool Used
    {
        get
        {
            lock (_gate)
            {
                return _used;
            }
        }
    }

    public bool Requested(PipelinePhase phase)
    {
        lock (_gate)
        {
            return _requested.Contains(phase);
        }
    }
}

/// <summary>
/// Warns, once the application has started, about features enabled in configuration whose phase no
/// <c>UseApiPipeline</c> call added. A warning: the application may add the middleware itself.
/// </summary>
internal sealed class UnwiredPipelineFeaturesWarning : IApiPipelineStartupCheck
{
    private readonly IServiceProvider _services;
    private readonly IEnumerable<PipelineFeature> _features;
    private readonly PipelineWiring _wiring;
    private readonly IHostApplicationLifetime? _lifetime;
    private readonly ILogger _logger;

    public UnwiredPipelineFeaturesWarning(
        IServiceProvider services,
        IEnumerable<PipelineFeature> features,
        PipelineWiring wiring,
        ILoggerFactory loggerFactory,
        // Optional: without a host there is no "started" moment.
        IHostApplicationLifetime? lifetime = null)
    {
        _services = services;
        _features = features;
        _wiring = wiring;
        _lifetime = lifetime;
        _logger = loggerFactory.CreateLogger(ApiPipelineBuilder.LoggerCategory);
    }

    public void Run() => _lifetime?.ApplicationStarted.Register(Check);

    private void Check()
    {
        var enabled = _features.Where(feature => feature.IsEnabled(_services)).ToList();
        if (enabled.Count == 0)
        {
            return;
        }

        if (!_wiring.Used)
        {
            _logger.UseApiPipelineNeverCalled(string.Join(", ", enabled.Select(f => f.ConfigurationKey)));
            return;
        }

        foreach (var feature in enabled.Where(f => !_wiring.Requested(f.Phase)))
        {
            _logger.FeatureNotInPipeline(feature.ConfigurationKey, feature.Phase);
        }
    }
}

internal static class PipelineFeatureServiceCollectionExtensions
{
    public static IServiceCollection AddPipelineFeature<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] TOptions>(
        this IServiceCollection services,
        PipelinePhase phase,
        string configurationKey,
        Func<TOptions, bool> isEnabled)
        where TOptions : class
    {
        services.AddSingleton(new PipelineFeature(
            phase,
            $"{ApiPipelineConfigurationKeys.Section}:{configurationKey}",
            sp => isEnabled(sp.GetRequiredService<IOptions<TOptions>>().Value)));
        return services;
    }
}
