using System.IO.Compression;
using System.Net.Mime;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.Options;

namespace ApiPipeline.NET.Options;

internal sealed class ConfigureResponseCompressionOptions : IConfigureOptions<ResponseCompressionOptions>
{
    private readonly IOptions<ResponseCompressionSettings> _settings;

    public ConfigureResponseCompressionOptions(IOptions<ResponseCompressionSettings> settings)
        => _settings = settings;

    public void Configure(ResponseCompressionOptions options)
    {
        var settings = _settings.Value;

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

        var mimeTypes = (settings.MimeTypes is { Length: > 0 }
                ? settings.MimeTypes
                : ResponseCompressionDefaults.MimeTypes.Concat([MediaTypeNames.Application.Json, "application/problem+json"]))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (settings.ExcludedMimeTypes is { Length: > 0 })
        {
            mimeTypes.RemoveAll(mt => settings.ExcludedMimeTypes.Contains(mt, StringComparer.OrdinalIgnoreCase));
        }

        options.MimeTypes = mimeTypes.ToArray();
    }
}
