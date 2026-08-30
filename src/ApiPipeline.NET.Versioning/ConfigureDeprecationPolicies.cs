using Asp.Versioning;
using Microsoft.Extensions.Options;

namespace ApiPipeline.NET.Versioning;

/// <summary>Adds the configured deprecation and sunset policies to the application's <c>Asp.Versioning</c> options.</summary>
internal sealed class ConfigureDeprecationPolicies : IConfigureOptions<ApiVersioningOptions>
{
    private readonly IOptions<ApiPipelineVersionDeprecationOptions> _deprecation;

    public ConfigureDeprecationPolicies(IOptions<ApiPipelineVersionDeprecationOptions> deprecation)
        => _deprecation = deprecation;

    public void Configure(ApiVersioningOptions options)
    {
        var settings = _deprecation.Value;
        if (!settings.Enabled)
        {
            return;
        }

        foreach (var deprecated in settings.DeprecatedVersions ?? [])
        {
            // Validated already; TryParse so a bypass skips one entry, not the host.
            if (!ApiVersionParser.Default.TryParse(deprecated.Version, out var apiVersion)
                || apiVersion is null)
            {
                continue;
            }

            var deprecation = options.Policies.Deprecate(apiVersion);
            if (deprecated.DeprecationDate is { } deprecationDate)
            {
                deprecation.Effective(deprecationDate);
            }

            if (!string.IsNullOrWhiteSpace(deprecated.DeprecationLink))
            {
                deprecation.Link(deprecated.DeprecationLink).Title("deprecation").Type("text/html");
            }

            // A separate policy: deprecation may come long before a sunset date.
            if (deprecated.SunsetDate is { } sunsetDate)
            {
                var sunset = options.Policies.Sunset(apiVersion).Effective(sunsetDate);
                if (!string.IsNullOrWhiteSpace(deprecated.SunsetLink))
                {
                    sunset.Link(deprecated.SunsetLink).Title("sunset").Type("text/html");
                }
            }
        }
    }
}
