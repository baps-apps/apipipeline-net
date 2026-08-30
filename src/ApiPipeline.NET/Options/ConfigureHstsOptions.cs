using Microsoft.AspNetCore.HttpsPolicy;
using Microsoft.Extensions.Options;

namespace ApiPipeline.NET;

/// <summary>HSTS settings for the framework's HSTS middleware, while security headers and HSTS are enabled.</summary>
internal sealed class ConfigureHstsOptions : IConfigureOptions<HstsOptions>
{
    private readonly IOptions<ApiPipelineSecurityHeadersOptions> _settings;

    public ConfigureHstsOptions(IOptions<ApiPipelineSecurityHeadersOptions> settings) => _settings = settings;

    public void Configure(HstsOptions options)
    {
        var settings = _settings.Value;
        if (!settings.Enabled || !settings.EnableStrictTransportSecurity)
        {
            return;
        }

        options.MaxAge = settings.StrictTransportSecurityMaxAge;
        options.IncludeSubDomains = settings.StrictTransportSecurityIncludeSubDomains;
        options.Preload = settings.StrictTransportSecurityPreload;

        if (!settings.StrictTransportSecurityExcludeLoopbackHosts)
        {
            options.ExcludedHosts.Clear();
        }

        foreach (var host in settings.StrictTransportSecurityExcludedHosts ?? [])
        {
            if (!string.IsNullOrWhiteSpace(host) && !options.ExcludedHosts.Contains(host, StringComparer.OrdinalIgnoreCase))
            {
                options.ExcludedHosts.Add(host.Trim());
            }
        }
    }
}
