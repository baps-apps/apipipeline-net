using System.Net;
using Microsoft.AspNetCore.RateLimiting;

namespace ApiPipeline.NET.RateLimiting;

/// <summary>
/// Whether a request is a health probe both limiters let through: a <c>GET</c> or <c>HEAD</c> (the
/// only methods an HTTP probe sends) to an excluded path, no proxy header (<c>X-Forwarded-For</c>,
/// <c>Forwarded</c>, <c>X-Original-For</c>), a peer in the probe networks (or none), and no explicit
/// endpoint policy. A probe path reached through the ingress, or written to, is limited.
/// </summary>
internal static class ProbeExemption
{
    /// <summary>Whether <paramref name="context"/> is exempt.</summary>
    /// <param name="context">The request, after routing.</param>
    /// <param name="excludedPaths">The probe paths.</param>
    /// <param name="probeNetworks">The networks a probe may come from.</param>
    public static bool Applies(HttpContext context, PathString[] excludedPaths, IPNetwork[] probeNetworks) =>
        IsProbeMethod(context.Request.Method)
        && IsExcludedPath(context.Request.Path, excludedPaths)
        && !CameThroughAProxy(context.Request.Headers)
        && FromProbeNetwork(context.Connection.RemoteIpAddress, probeNetworks)
        && context.GetEndpoint()?.Metadata.GetMetadata<EnableRateLimitingAttribute>() is null;

    // An endpoint that changes state under a probe path (POST /health/reset) is not a probe.
    private static bool IsProbeMethod(string method) => HttpMethods.IsGet(method) || HttpMethods.IsHead(method);

    private static bool FromProbeNetwork(IPAddress? address, IPNetwork[] probeNetworks)
    {
        if (address is null)
        {
            return true;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        foreach (var network in probeNetworks)
        {
            if (network.Contains(address))
            {
                return true;
            }
        }

        return false;
    }

    private static bool CameThroughAProxy(IHeaderDictionary headers) =>
        headers.ContainsKey("X-Forwarded-For")
        || headers.ContainsKey("X-Original-For")
        || headers.ContainsKey("Forwarded");

    private static bool IsExcludedPath(PathString path, PathString[] excludedPaths)
    {
        foreach (var candidate in excludedPaths)
        {
            if (path.StartsWithSegments(candidate, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
