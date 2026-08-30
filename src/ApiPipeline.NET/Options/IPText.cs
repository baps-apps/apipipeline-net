using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;

namespace ApiPipeline.NET;

/// <summary>
/// Parses configured addresses and CIDR ranges strictly. <see cref="IPAddress.TryParse(string?, out IPAddress?)"/>
/// also reads shorthand, octal and hex IPv4 (<c>10.0.10</c> is 10.0.0.10, <c>010.0.0.1</c> is 8.0.0.1,
/// <c>10/8</c> is 0.0.0.0/8), so a typo in a trust list would trust the wrong addresses. IPv4 must
/// be four decimal parts as written by <see cref="IPAddress.ToString"/>; surrounding spaces are ignored.
/// </summary>
internal static class IPText
{
    /// <summary>Parses one address: dotted-decimal IPv4 or IPv6.</summary>
    public static bool TryParseAddress(string? text, [NotNullWhen(true)] out IPAddress? address)
    {
        address = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.Trim();
        if (!IPAddress.TryParse(trimmed, out var parsed))
        {
            return false;
        }

        if (parsed.AddressFamily == AddressFamily.InterNetwork
            && !string.Equals(parsed.ToString(), trimmed, StringComparison.Ordinal))
        {
            return false;
        }

        address = parsed;
        return true;
    }

    /// <summary>
    /// Parses one CIDR range (<c>10.0.0.0/8</c>). Host bits are masked, as the framework does:
    /// <c>10.1.2.3/8</c> is 10.0.0.0/8.
    /// </summary>
    public static bool TryParseNetwork(string? text, out System.Net.IPNetwork network)
    {
        network = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.Trim();
        var slash = trimmed.IndexOf('/', StringComparison.Ordinal);
        return slash > 0
            && TryParseAddress(trimmed[..slash], out _)
            && System.Net.IPNetwork.TryParse(trimmed, out network);
    }
}
