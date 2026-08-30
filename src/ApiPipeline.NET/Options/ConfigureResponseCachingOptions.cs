using Microsoft.AspNetCore.ResponseCaching;
using Microsoft.Extensions.Options;

namespace ApiPipeline.NET;

/// <summary>
/// Applies <see cref="ApiPipelineResponseCachingOptions"/> to the framework's
/// <see cref="ResponseCachingOptions"/>, only while the feature is enabled: with it off, an
/// application's own response caching settings are left alone.
/// </summary>
internal sealed class ConfigureResponseCachingOptions : IConfigureOptions<ResponseCachingOptions>
{
    private readonly IOptions<ApiPipelineResponseCachingOptions> _settings;

    public ConfigureResponseCachingOptions(IOptions<ApiPipelineResponseCachingOptions> settings)
        => _settings = settings;

    public void Configure(ResponseCachingOptions options)
    {
        var settings = _settings.Value;
        if (!settings.Enabled)
        {
            return;
        }

        if (settings.SizeLimitBytes is { } size)
        {
            options.SizeLimit = size;
        }

        options.UseCaseSensitivePaths = settings.UseCaseSensitivePaths;
    }
}
