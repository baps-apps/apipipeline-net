using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Options;

namespace ApiPipeline.NET.Options;

/// <summary>
/// Applies <see cref="RequestLimitsOptions"/> and <see cref="ForwardedHeadersSettings.SuppressServerHeader"/>
/// to Kestrel server options via the options infrastructure, ensuring validated options are used (not raw configuration).
/// Registered automatically by <see cref="Extensions.ServiceCollectionExtensions.AddRequestLimits"/>.
/// </summary>
internal sealed class ConfigureKestrelOptions : IConfigureOptions<KestrelServerOptions>
{
    private readonly IOptions<RequestLimitsOptions> _requestLimits;
    private readonly IOptions<ForwardedHeadersSettings> _forwardedHeaders;

    public ConfigureKestrelOptions(
        IOptions<RequestLimitsOptions> requestLimits,
        IOptions<ForwardedHeadersSettings> forwardedHeaders)
    {
        _requestLimits = requestLimits;
        _forwardedHeaders = forwardedHeaders;
    }

    public void Configure(KestrelServerOptions options)
    {
        if (_forwardedHeaders.Value.SuppressServerHeader)
        {
            options.AddServerHeader = false;
        }

        var limits = _requestLimits.Value;
        if (!limits.Enabled)
        {
            return;
        }

        if (limits.MaxRequestBodySize is { } maxBody)
        {
            options.Limits.MaxRequestBodySize = maxBody;
        }

        if (limits.MaxRequestHeadersTotalSize is { } maxHeadersTotal)
        {
            options.Limits.MaxRequestHeadersTotalSize = maxHeadersTotal;
        }

        if (limits.MaxRequestHeaderCount is { } maxHeaderCount)
        {
            options.Limits.MaxRequestHeaderCount = maxHeaderCount;
        }
    }
}
