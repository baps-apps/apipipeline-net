using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Options;

namespace ApiPipeline.NET.Options;

/// <summary>
/// Applies <see cref="RequestLimitsOptions"/> to Kestrel server limits via the
/// options infrastructure, ensuring validated options are used (not raw configuration).
/// Registered automatically by <see cref="Extensions.ServiceCollectionExtensions.AddRequestLimits"/>.
/// </summary>
internal sealed class ConfigureKestrelOptions : IConfigureOptions<KestrelServerOptions>
{
    private readonly IOptions<RequestLimitsOptions> _requestLimits;

    public ConfigureKestrelOptions(IOptions<RequestLimitsOptions> requestLimits)
        => _requestLimits = requestLimits;

    public void Configure(KestrelServerOptions options)
    {
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
