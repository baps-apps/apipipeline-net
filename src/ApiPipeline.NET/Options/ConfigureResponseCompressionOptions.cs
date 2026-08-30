using System.Net.Mime;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.Options;

namespace ApiPipeline.NET;

/// <summary>Response compression settings, while enabled.</summary>
internal sealed class ConfigureResponseCompressionOptions : IConfigureOptions<ResponseCompressionOptions>
{
    private readonly IOptions<ApiPipelineResponseCompressionOptions> _settings;

    public ConfigureResponseCompressionOptions(IOptions<ApiPipelineResponseCompressionOptions> settings)
        => _settings = settings;

    public void Configure(ResponseCompressionOptions options)
    {
        var settings = _settings.Value;
        if (!settings.Enabled)
        {
            return;
        }

        options.EnableForHttps = settings.EnableForHttps;

        options.Providers.Clear();
        if (settings.EnableBrotli)
        {
            options.Providers.Add<BrotliCompressionProvider>();
        }
        if (settings.EnableGzip)
        {
            options.Providers.Add<GzipCompressionProvider>();
        }

        var mimeTypes = (settings.MimeTypes is { Count: > 0 }
                ? (IEnumerable<string>)settings.MimeTypes
                : ResponseCompressionDefaults.MimeTypes.Concat([MediaTypeNames.Application.Json, "application/problem+json"]))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (settings.ExcludedMimeTypes is { Count: > 0 })
        {
            mimeTypes.RemoveAll(mt => settings.ExcludedMimeTypes.Contains(mt, StringComparer.OrdinalIgnoreCase));
        }

        options.MimeTypes = mimeTypes.ToArray();
    }
}
