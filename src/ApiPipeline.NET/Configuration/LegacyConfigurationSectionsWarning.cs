using ApiPipeline.NET.Observability;

namespace ApiPipeline.NET.Configuration;

/// <summary>
/// Warns when unread top-level sections (<c>CorsOptions</c>, <c>RateLimitingOptions</c>, ...) are
/// present and the matching <c>ApiPipeline:*</c> section is not: those features would silently run
/// on defaults. A warning, since the application may own a section of that name.
/// </summary>
internal sealed class LegacyConfigurationSectionsWarning : IApiPipelineStartupCheck
{
    private readonly IConfiguration _root;
    private readonly ILogger<LegacyConfigurationSectionsWarning> _logger;

    public LegacyConfigurationSectionsWarning(IConfiguration root, ILogger<LegacyConfigurationSectionsWarning> logger)
    {
        _root = root;
        _logger = logger;
    }

    public void Run()
    {
        var pipeline = _root.GetSection(ApiPipelineConfigurationKeys.Section);
        var stale = ApiPipelineConfigurationKeys.LegacyRootSections
            .Where(entry => _root.GetSection(entry.Key).Exists() && !pipeline.GetSection(entry.Value).Exists())
            .Select(entry => $"{entry.Key} -> {ApiPipelineConfigurationKeys.Section}:{entry.Value}")
            .ToArray();

        if (stale.Length == 0)
        {
            return;
        }

        _logger.LegacySectionsFound(ApiPipelineConfigurationKeys.Section, string.Join(", ", stale));
    }
}
