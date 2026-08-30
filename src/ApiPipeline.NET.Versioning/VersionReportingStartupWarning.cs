using Asp.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ApiPipeline.NET.Versioning;

/// <summary>
/// Warns when deprecation policies are configured but version reporting is off globally, so they
/// may emit nothing. A warning: per-endpoint <c>ReportApiVersions()</c> is invisible here.
/// </summary>
internal sealed class VersionReportingStartupWarning : IApiPipelineStartupCheck
{
    private readonly IOptions<ApiPipelineVersionDeprecationOptions> _deprecation;
    private readonly IOptions<ApiVersioningOptions> _versioning;
    private readonly ILogger<VersionReportingStartupWarning> _logger;

    public VersionReportingStartupWarning(
        IOptions<ApiPipelineVersionDeprecationOptions> deprecation,
        IOptions<ApiVersioningOptions> versioning,
        ILogger<VersionReportingStartupWarning> logger)
    {
        _deprecation = deprecation;
        _versioning = versioning;
        _logger = logger;
    }

    public void Run()
    {
        var settings = _deprecation.Value;
        var hasPolicies = settings.Enabled && (settings.DeprecatedVersions ?? []).Count > 0;

        if (hasPolicies && !_versioning.Value.ReportApiVersions)
        {
            _logger.VersionReportingOff();
        }
    }
}
