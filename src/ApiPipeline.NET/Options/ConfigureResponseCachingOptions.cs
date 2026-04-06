using Microsoft.AspNetCore.ResponseCaching;
using Microsoft.Extensions.Options;

namespace ApiPipeline.NET.Options;

internal sealed class ConfigureResponseCachingOptions : IConfigureOptions<ResponseCachingOptions>
{
    private readonly IOptions<ResponseCachingSettings> _settings;

    public ConfigureResponseCachingOptions(IOptions<ResponseCachingSettings> settings)
        => _settings = settings;

    public void Configure(ResponseCachingOptions options)
    {
        var settings = _settings.Value;
        if (settings.SizeLimitBytes is { } size)
        {
            options.SizeLimit = size;
        }

        options.UseCaseSensitivePaths = settings.UseCaseSensitivePaths;
    }
}
