using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ApiPipeline.NET.Versioning;

/// <summary>
/// Warns when the unread top-level <c>ApiVersionDeprecationOptions</c> section is present without
/// <c>ApiPipeline:VersionDeprecation</c>: every deprecation header would silently be dropped.
/// </summary>
internal sealed class LegacyVersionDeprecationSectionWarning : IApiPipelineStartupCheck
{
    private readonly IConfiguration _root;
    private readonly ILogger<LegacyVersionDeprecationSectionWarning> _logger;

    public LegacyVersionDeprecationSectionWarning(
        IConfiguration root,
        ILogger<LegacyVersionDeprecationSectionWarning> logger)
    {
        _root = root;
        _logger = logger;
    }

    public void Run()
    {
        var current = _root.GetSection(VersioningConfigurationKeys.Section)
            .GetSection(VersioningConfigurationKeys.ApiVersionDeprecation);

        if (_root.GetSection(VersioningConfigurationKeys.LegacyApiVersionDeprecation).Exists() && !current.Exists())
        {
            _logger.LegacyVersionDeprecationSection(
                VersioningConfigurationKeys.Section,
                VersioningConfigurationKeys.ApiVersionDeprecation,
                VersioningConfigurationKeys.LegacyApiVersionDeprecation);
        }
    }
}
