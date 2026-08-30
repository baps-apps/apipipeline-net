using System.Diagnostics.CodeAnalysis;
using System.IO.Compression;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.Options;

namespace ApiPipeline.NET;

/// <summary>
/// Registers the package's configuration of a framework options type first, so any configuration
/// the application registers, before or after, overwrites it: options are configured in order.
/// </summary>
internal static class FrameworkDefaults
{
    /// <summary>Inserts <typeparamref name="TConfigure"/> as the first configuration, once.</summary>
    public static IServiceCollection AddFrameworkDefaults<
        TOptions,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TConfigure>(
        this IServiceCollection services)
        where TOptions : class
        where TConfigure : class, IConfigureOptions<TOptions>
    {
        foreach (var descriptor in services)
        {
            if (descriptor.ServiceType == typeof(IConfigureOptions<TOptions>)
                && descriptor.ImplementationType == typeof(TConfigure))
            {
                return services;
            }
        }

        services.Insert(0, ServiceDescriptor.Singleton<IConfigureOptions<TOptions>, TConfigure>());
        return services;
    }
}

/// <summary>Form body and value-count limits, while request limits are enabled.</summary>
internal sealed class ConfigureFormOptions : IConfigureOptions<FormOptions>
{
    private readonly IOptions<ApiPipelineRequestLimitsOptions> _requestLimits;

    public ConfigureFormOptions(IOptions<ApiPipelineRequestLimitsOptions> requestLimits) => _requestLimits = requestLimits;

    public void Configure(FormOptions options)
    {
        var limits = _requestLimits.Value;
        if (!limits.Enabled)
        {
            return;
        }

        if (limits.MaxRequestBodySize is { } maxBody)
        {
            options.MultipartBodyLengthLimit = maxBody;
            options.BufferBodyLengthLimit = maxBody;
        }

        if (limits.MaxFormValueCount is { } maxValueCount)
        {
            options.ValueCountLimit = maxValueCount;
        }
    }
}

/// <summary>Brotli at <see cref="CompressionLevel.Fastest"/> while compression is enabled.</summary>
internal sealed class ConfigureBrotliCompressionLevel : IConfigureOptions<BrotliCompressionProviderOptions>
{
    private readonly IOptions<ApiPipelineResponseCompressionOptions> _settings;

    public ConfigureBrotliCompressionLevel(IOptions<ApiPipelineResponseCompressionOptions> settings) => _settings = settings;

    public void Configure(BrotliCompressionProviderOptions options)
    {
        if (_settings.Value.Enabled)
        {
            options.Level = CompressionLevel.Fastest;
        }
    }
}

/// <summary>Gzip at <see cref="CompressionLevel.Fastest"/> while compression is enabled.</summary>
internal sealed class ConfigureGzipCompressionLevel : IConfigureOptions<GzipCompressionProviderOptions>
{
    private readonly IOptions<ApiPipelineResponseCompressionOptions> _settings;

    public ConfigureGzipCompressionLevel(IOptions<ApiPipelineResponseCompressionOptions> settings) => _settings = settings;

    public void Configure(GzipCompressionProviderOptions options)
    {
        if (_settings.Value.Enabled)
        {
            options.Level = CompressionLevel.Fastest;
        }
    }
}
