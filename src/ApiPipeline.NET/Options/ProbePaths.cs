using System.ComponentModel.DataAnnotations;

namespace ApiPipeline.NET;

/// <summary>Default probe paths and networks, and the rules for every <c>ExcludedPaths</c> list.</summary>
internal static class ProbePaths
{
    public static IReadOnlyList<string> Default { get; } = ["/health", "/internal/health"];

    /// <summary>Loopback, RFC 1918, RFC 6598, and IPv6 loopback, ULA and link-local.</summary>
    public static IReadOnlyList<string> DefaultNetworks { get; } =
    [
        "127.0.0.0/8", "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "100.64.0.0/10",
        "::1/128", "fc00::/7", "fe80::/10",
    ];

    /// <summary><paramref name="configured"/> when set (even empty), else the defaults; unparsed entries skipped.</summary>
    public static System.Net.IPNetwork[] ResolveNetworks(IList<string>? configured)
    {
        var result = new List<System.Net.IPNetwork>();
        foreach (var entry in configured ?? (IEnumerable<string>)DefaultNetworks)
        {
            if (IPText.TryParseNetwork(entry, out var network))
            {
                result.Add(network);
            }
        }

        return [.. result];
    }

    /// <summary>A failure per entry that is not a CIDR range.</summary>
    public static IEnumerable<ValidationResult> ValidateNetworks(IList<string>? networks, string memberName)
    {
        foreach (var network in networks ?? [])
        {
            if (!IPText.TryParseNetwork(network, out _))
            {
                yield return new ValidationResult(
                    $"{memberName} entry '{network}' is not a CIDR range. Use a network address and prefix " +
                    "length, for example \"10.0.0.0/8\" or \"fd00::/8\", with IPv4 written as four decimal " +
                    "numbers (\"10/8\" and \"010.0.0.0/8\" are read as other networks).",
                    [memberName]);
            }
        }
    }

    /// <summary><paramref name="configured"/> when set (even empty), else <paramref name="defaults"/>; trimmed, blanks skipped.</summary>
    public static PathString[] Resolve(IList<string>? configured, IReadOnlyList<string> defaults) =>
        [.. (configured ?? (IEnumerable<string>)defaults)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => new PathString(p.Trim()))];

    /// <summary>A failure per entry that is not a path.</summary>
    public static IEnumerable<ValidationResult> Validate(IList<string>? paths, string memberName)
    {
        foreach (var path in paths ?? [])
        {
            if (string.IsNullOrWhiteSpace(path) || !path.Trim().StartsWith('/'))
            {
                yield return new ValidationResult(
                    $"{memberName} entry '{path}' is not a path. Each entry must start with '/', " +
                    "for example \"/internal/health\".",
                    [memberName]);
            }
        }
    }
}
