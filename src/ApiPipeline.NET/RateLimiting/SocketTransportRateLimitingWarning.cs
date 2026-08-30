using ApiPipeline.NET.Configuration;
using ApiPipeline.NET.Observability;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Options;

namespace ApiPipeline.NET.RateLimiting;

/// <summary>
/// Warns when the server listens on a Unix socket or named pipe while <c>AnonymousFallback</c> is
/// <see cref="AnonymousFallbackBehavior.Reject"/>: no request has a client IP, so every anonymous
/// request gets 429, probes included. Checked once the server has started and its addresses are known.
/// </summary>
internal sealed class SocketTransportRateLimitingWarning : IApiPipelineStartupCheck
{
    internal const string Message =
        "Rate limiting: the server is listening on {Address}, a Unix domain socket or named pipe, so " +
        "requests carry no client IP address. ApiPipeline:RateLimiting:AnonymousFallback is Reject, so " +
        "every request without an authenticated user or an allow-listed API key will get 429 Too Many " +
        "Requests, health probes included. Forwarded headers do not help: they are not applied to a " +
        "connection without a peer address. Listen on TCP loopback instead (for example " +
        "http://127.0.0.1:8080) and trust 127.0.0.1 in ApiPipeline:ForwardedHeaders:KnownProxies, or " +
        "set ApiPipeline:RateLimiting:AnonymousFallback to RateLimit or Allow if limiting by IP is not needed.";

    private readonly IOptions<ApiPipelineRateLimitingOptions> _settings;
    private readonly IHostApplicationLifetime? _lifetime;
    private readonly IServer? _server;
    private readonly ILogger<SocketTransportRateLimitingWarning> _logger;

    public SocketTransportRateLimitingWarning(
        IOptions<ApiPipelineRateLimitingOptions> settings,
        ILogger<SocketTransportRateLimitingWarning> logger,
        // Optional: a plain generic host has neither.
        IHostApplicationLifetime? lifetime = null,
        IServer? server = null)
    {
        _settings = settings;
        _logger = logger;
        _lifetime = lifetime;
        _server = server;
    }

    public void Run()
    {
        var settings = _settings.Value;
        if (!settings.Enabled
            || settings.AnonymousFallback != AnonymousFallbackBehavior.Reject
            || _lifetime is null
            || _server is null)
        {
            return;
        }

        _lifetime.ApplicationStarted.Register(CheckAddresses);
    }

    /// <summary>Kestrel's <c>unix:</c> and <c>pipe:</c> address forms.</summary>
    internal static bool HasNoPeerAddress(string address)
    {
        var separator = address.IndexOf("://", StringComparison.Ordinal);
        if (separator < 0)
        {
            return false;
        }

        var authority = address.AsSpan(separator + 3);
        return authority.StartsWith("unix:", StringComparison.OrdinalIgnoreCase)
            || authority.StartsWith("pipe:", StringComparison.OrdinalIgnoreCase);
    }

    private void CheckAddresses()
    {
        var addresses = _server!.Features.Get<IServerAddressesFeature>()?.Addresses;
        var address = addresses?.FirstOrDefault(HasNoPeerAddress);
        if (address is not null)
        {
            _logger.RateLimitingOnSocketTransport(address);
        }
    }
}
