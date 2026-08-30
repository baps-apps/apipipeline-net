using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.Options;

namespace ApiPipeline.NET;

/// <summary>Output cache store settings, while enabled; unset values keep the framework's.</summary>
internal sealed class ConfigureOutputCacheOptions : IConfigureOptions<OutputCacheOptions>
{
    private readonly IOptions<ApiPipelineOutputCachingOptions> _settings;

    public ConfigureOutputCacheOptions(IOptions<ApiPipelineOutputCachingOptions> settings)
        => _settings = settings;

    public void Configure(OutputCacheOptions options)
    {
        var settings = _settings.Value;
        if (!settings.Enabled)
        {
            return;
        }

        if (settings.DefaultExpiration is { } expiration)
        {
            options.DefaultExpirationTimeSpan = expiration;
        }

        if (settings.SizeLimitBytes is { } size)
        {
            options.SizeLimit = size;
        }

        if (settings.MaximumBodySizeBytes is { } body)
        {
            options.MaximumBodySize = body;
        }

        options.UseCaseSensitivePaths = settings.UseCaseSensitivePaths;
    }
}
