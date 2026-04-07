using System.ComponentModel.DataAnnotations;

namespace ApiPipeline.NET.Options;

/// <summary>
/// Configuration options for forwarded headers processing behind reverse proxies.
/// When <see cref="Enabled"/> is <c>false</c>, forwarded headers middleware is skipped entirely.
/// <para>
/// <b>Security note:</b> In production, always configure <see cref="KnownProxies"/> or
/// <see cref="KnownNetworks"/> to restrict which proxies are trusted. Leaving both empty
/// defaults to trusting only the loopback address, which will cause <c>X-Forwarded-For</c>
/// to be ignored behind a real load balancer.
/// </para>
/// </summary>
public sealed class ForwardedHeadersSettings
{
    /// <summary>
    /// Whether forwarded headers middleware is enabled.
    /// When <c>false</c>, the middleware is skipped entirely and headers such as
    /// <c>X-Forwarded-For</c> and <c>X-Forwarded-Proto</c> are not processed.
    /// Defaults to <c>true</c>.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Maximum number of proxy entries to process from <c>X-Forwarded-For</c>.
    /// Set to the number of trusted proxies in front of the application.
    /// Common topologies: single reverse proxy = 1, Nginx Ingress on Kubernetes = 2,
    /// CloudFront → ALB → Nginx → Pod = 3-4. Maximum 20.
    /// Defaults to <c>1</c>.
    /// </summary>
    [Range(1, 20)]
    public int ForwardLimit { get; set; } = 1;

    /// <summary>
    /// IP addresses of known proxies to trust (e.g. <c>["10.0.0.1", "172.16.0.1"]</c>).
    /// When empty, ASP.NET Core defaults to trusting only the loopback address.
    /// </summary>
    [MinLength(0)]
    public string[]? KnownProxies { get; set; }

    /// <summary>
    /// CIDR network ranges of known proxies to trust (e.g. <c>["10.0.0.0/8", "172.16.0.0/12"]</c>).
    /// Useful in container orchestrators where proxy IPs are dynamic within a known subnet.
    /// </summary>
    [MinLength(0)]
    public string[]? KnownNetworks { get; set; }

    /// <summary>
    /// When <c>true</c>, clears the default known proxies and networks before applying
    /// <see cref="KnownProxies"/> and <see cref="KnownNetworks"/>, allowing traffic from
    /// any upstream proxy listed. Required in most cloud/container deployments.
    /// </summary>
    public bool ClearDefaultProxies { get; set; } = false;

    /// <summary>
    /// When <c>true</c> in Production, startup fails if forwarded headers are enabled but no
    /// trusted proxies/networks are configured and <see cref="ClearDefaultProxies"/> is false.
    /// This prevents silent fallback to proxy IPs, which can break client identity and cause
    /// shared-bucket rate limiting behavior.
    /// </summary>
    public bool EnforceTrustedProxyConfigurationInProduction { get; set; } = true;

    /// <summary>
    /// When <c>true</c>, suppresses the <c>Server: Kestrel</c> response header to prevent
    /// server fingerprinting. Recommended for production deployments.
    /// </summary>
    public bool SuppressServerHeader { get; set; } = true;
}
