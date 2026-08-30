using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Options;

namespace ApiPipeline.NET;

/// <summary>Applies request limits to Kestrel; <c>SuppressServerHeader</c> applies even with limits off.</summary>
internal sealed class ConfigureKestrelOptions : IConfigureOptions<KestrelServerOptions>
{
    private readonly IOptions<ApiPipelineRequestLimitsOptions> _requestLimits;

    public ConfigureKestrelOptions(IOptions<ApiPipelineRequestLimitsOptions> requestLimits)
    {
        _requestLimits = requestLimits;
    }

    public void Configure(KestrelServerOptions options)
    {
        var limits = _requestLimits.Value;

        if (limits.SuppressServerHeader)
        {
            options.AddServerHeader = false;
        }

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

        if (limits.RequestHeadersTimeout is { } headersTimeout)
        {
            options.Limits.RequestHeadersTimeout = headersTimeout;
        }

        if (limits.KeepAliveTimeout is { } keepAlive)
        {
            options.Limits.KeepAliveTimeout = keepAlive;
        }

        if (limits.MaxConcurrentConnections is { } maxConnections)
        {
            options.Limits.MaxConcurrentConnections = maxConnections;
        }

        if (limits.MinRequestBodyDataRateBytesPerSecond is { } bytesPerSecond)
        {
            // 0 turns the check off: a null MinDataRate (a 0 B/s MinDataRate throws).
            options.Limits.MinRequestBodyDataRate = bytesPerSecond <= 0
                ? null
                : new MinDataRate(
                    bytesPerSecond,
                    limits.MinRequestBodyDataRateGracePeriod ?? TimeSpan.FromSeconds(5));
        }
    }
}
