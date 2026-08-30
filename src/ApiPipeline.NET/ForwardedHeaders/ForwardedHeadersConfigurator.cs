using ApiPipeline.NET.Configuration;
using ApiPipeline.NET.Observability;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Hosting;

namespace ApiPipeline.NET.ForwardedHeaders;

/// <summary>Builds the framework's <see cref="ForwardedHeadersOptions"/> and guards unsafe trust sets.</summary>
internal static class ForwardedHeadersConfigurator
{
    internal sealed record Result(ForwardedHeadersOptions Options, bool HasEffectiveTrustSet);

    /// <summary>
    /// Builds the options from validated trust lists; outside Development, throws for a configuration
    /// that lets callers choose client IP, scheme or host.
    /// </summary>
    /// <param name="settings">The validated settings.</param>
    /// <param name="allowedHosts">The effective <c>AllowedHosts</c>.</param>
    /// <param name="environment">Decides warn or throw.</param>
    /// <param name="logger">Gets the warnings.</param>
    internal static Result Build(
        ApiPipelineForwardedHeadersOptions settings,
        IEnumerable<string>? allowedHosts,
        IHostEnvironment environment,
        ILogger logger)
    {
        var forwarded = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor
            | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto;

        var hostAllowList = HostAllowList(allowedHosts);

        if (settings.ProcessHost)
        {
            GuardHostProcessing(hostAllowList, environment, logger);
            forwarded |= Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedHost;
        }

        var options = new ForwardedHeadersOptions
        {
            ForwardedHeaders = forwarded,
            ForwardLimit = settings.ForwardLimit
        };

        // Host filtering only sees the connection's host, so check the forwarded one too.
        if (settings.ProcessHost && hostAllowList is not null)
        {
            options.AllowedHosts = hostAllowList;
        }

        if (settings.ClearDefaultProxies)
        {
            options.KnownProxies.Clear();
            options.KnownIPNetworks.Clear();
        }

        var configuredTrustEntries = AddKnownProxies(settings, options) + AddKnownNetworks(settings, options);

        var hasEffectiveTrustSet = options.KnownProxies.Count > 0 || options.KnownIPNetworks.Count > 0;
        GuardTrustSet(settings, environment, logger, hasEffectiveTrustSet, configuredTrustEntries > 0);
        GuardTrustEveryClient(settings, environment, logger, options.KnownIPNetworks);

        return new Result(options, hasEffectiveTrustSet);
    }

    // Options validation has rejected every entry that does not parse; the checks only satisfy the
    // compiler and keep a bypass (options built without validation) from trusting a misread entry.
    private static int AddKnownProxies(ApiPipelineForwardedHeadersOptions settings, ForwardedHeadersOptions options)
    {
        var added = 0;
        foreach (var proxy in settings.KnownProxies ?? [])
        {
            if (IPText.TryParseAddress(proxy, out var ip))
            {
                options.KnownProxies.Add(ip);
                added++;
            }
        }

        return added;
    }

    private static int AddKnownNetworks(ApiPipelineForwardedHeadersOptions settings, ForwardedHeadersOptions options)
    {
        var added = 0;
        foreach (var network in settings.KnownNetworks ?? [])
        {
            // Masks host bits: 10.1.2.3/8 becomes 10.0.0.0/8.
            if (IPText.TryParseNetwork(network, out var parsed))
            {
                options.KnownIPNetworks.Add(parsed);
                added++;
            }
        }

        return added;
    }

    /// <summary>
    /// Fails outside Development on an empty trust set (every caller trusted) or a loopback-only one
    /// (the real proxy ignored).
    /// </summary>
    private static void GuardTrustSet(
        ApiPipelineForwardedHeadersOptions settings,
        IHostEnvironment environment,
        ILogger logger,
        bool hasEffectiveTrustSet,
        bool hasConfiguredTrustSet)
    {
        if (!hasEffectiveTrustSet)
        {
            Report(
                settings,
                environment,
                logger,
                "ForwardedHeaders is enabled but the effective trusted-proxy set is empty. " +
                "ForwardedHeadersMiddleware skips its trust check entirely when no KnownProxies and no " +
                "KnownNetworks are configured, so X-Forwarded-For, X-Forwarded-Proto, and X-Forwarded-Host " +
                "would be accepted from any caller — making the client IP, request scheme, and host " +
                "attacker-controlled, and IP-partitioned rate limiting trivially bypassable. " +
                "Configure ApiPipeline:ForwardedHeaders:KnownNetworks (for example [\"10.0.0.0/8\"]) or " +
                "ApiPipeline:ForwardedHeaders:KnownProxies for your ingress. To explicitly accept this unsafe " +
                "behavior, set ApiPipeline:ForwardedHeaders:EnforceTrustedProxyConfiguration=false.");
            return;
        }

        if (!hasConfiguredTrustSet)
        {
            Report(
                settings,
                environment,
                logger,
                "ForwardedHeaders is enabled but no KnownProxies or KnownNetworks are configured, so only " +
                "the loopback address is trusted. Behind a reverse proxy (Kubernetes ingress, Nginx, ALB) " +
                "X-Forwarded-For will be ignored and RemoteIpAddress will be the proxy's IP, collapsing " +
                "rate limiting into a single shared bucket for all clients. Configure " +
                "ApiPipeline:ForwardedHeaders:KnownNetworks or ApiPipeline:ForwardedHeaders:KnownProxies for your " +
                "deployment topology, or set " +
                "ApiPipeline:ForwardedHeaders:EnforceTrustedProxyConfiguration=false to explicitly " +
                "accept this behavior.");
        }
    }

    /// <summary>Fails outside Development on trusted networks that, alone or together, contain every client address.</summary>
    private static void GuardTrustEveryClient(
        ApiPipelineForwardedHeadersOptions settings,
        IHostEnvironment environment,
        ILogger logger,
        IList<System.Net.IPNetwork> networks)
    {
        var covering = EveryClientNetworks(networks);
        if (covering.Count == 0)
        {
            return;
        }

        var entries = string.Join(", ", covering.Select(network => $"'{network}'"));
        var claim = covering.Count == 1 ? $"entry {entries} contains" : $"entries {entries} together contain";
        Report(
            settings,
            environment,
            logger,
            $"ApiPipeline:ForwardedHeaders:KnownNetworks {claim} every client address, so any caller can set " +
            "X-Forwarded-For and X-Forwarded-Proto: the client IP and request scheme become " +
            "attacker-controlled, and IP-partitioned rate limiting is trivially bypassable. List only your " +
            "proxies' addresses or networks, as the pod sees them. To explicitly accept this unsafe " +
            "behavior, set ApiPipeline:ForwardedHeaders:EnforceTrustedProxyConfiguration=false.");
    }

    /// <summary>
    /// The networks that contain every IPv4 address, directly or IPv4-mapped (as dual-stack sockets
    /// report IPv4 clients): one network alone if one does, else all that together do; empty if none
    /// do. Networks covering all of IPv6 cover the mapped range too, so they are found here as well.
    /// </summary>
    private static List<System.Net.IPNetwork> EveryClientNetworks(IList<System.Net.IPNetwork> networks)
    {
        // IPv4 placed in the mapped range ::ffff:0:0/96; an IPv6 network counts for the part it shares with it.
        var ranges = new List<(System.Net.IPNetwork Network, UInt128 First, UInt128 Last)>();
        foreach (var network in networks)
        {
            var (first, last) = Range(network);
            if (first <= IPv4MappedLast && last >= IPv4MappedFirst)
            {
                ranges.Add((network, UInt128.Max(first, IPv4MappedFirst), UInt128.Min(last, IPv4MappedLast)));
            }
        }

        var alone = ranges
            .Where(range => range.First == IPv4MappedFirst && range.Last == IPv4MappedLast)
            .Select(range => range.Network)
            .ToList();
        if (alone.Count > 0)
        {
            return alone;
        }

        return Covers(ranges) ? [.. ranges.Select(range => range.Network).Distinct()] : [];
    }

    /// <summary>Whether the ranges together leave no gap in the mapped range.</summary>
    private static bool Covers(List<(System.Net.IPNetwork Network, UInt128 First, UInt128 Last)> ranges)
    {
        var next = IPv4MappedFirst;
        foreach (var (_, first, last) in ranges.OrderBy(range => range.First))
        {
            if (first > next)
            {
                return false;
            }

            if (last == IPv4MappedLast)
            {
                return true;
            }

            next = UInt128.Max(next, last + 1);
        }

        return false;
    }

    /// <summary>The first and last address of <paramref name="network"/>, IPv4 placed in the mapped range.</summary>
    private static (UInt128 First, UInt128 Last) Range(System.Net.IPNetwork network)
    {
        Span<byte> bytes = stackalloc byte[16];
        network.BaseAddress.TryWriteBytes(bytes, out var written);
        if (written == 4)
        {
            var hostBits = 32 - network.PrefixLength;
            var start = (UInt128)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes) >> hostBits << hostBits;
            return (IPv4MappedFirst + start, IPv4MappedFirst + start + ((UInt128.One << hostBits) - 1));
        }

        var hostMask = network.PrefixLength == 0 ? UInt128.MaxValue : UInt128.MaxValue >> network.PrefixLength;
        var address = System.Buffers.Binary.BinaryPrimitives.ReadUInt128BigEndian(bytes);
        return (address & ~hostMask, address | hostMask);
    }

    /// <summary><c>::ffff:0.0.0.0</c>, the first IPv4-mapped address.</summary>
    private static readonly UInt128 IPv4MappedFirst = (UInt128)0xFFFF << 32;

    /// <summary><c>::ffff:255.255.255.255</c>, the last IPv4-mapped address.</summary>
    private static readonly UInt128 IPv4MappedLast = IPv4MappedFirst + uint.MaxValue;

    /// <summary>Fails outside Development when <c>ProcessHost</c> is on without a real <c>AllowedHosts</c> list.</summary>
    private static void GuardHostProcessing(
        IList<string>? hostAllowList,
        IHostEnvironment environment,
        ILogger logger)
    {
        if (hostAllowList is not null)
        {
            return;
        }

        const string Message =
            "ApiPipeline:ForwardedHeaders:ProcessHost is true but AllowedHosts is unset, empty, or \"*\", so " +
            "nothing constrains the host this application will accept. X-Forwarded-Host is copied from " +
            "the caller's own Host header by the common ingress controllers (nginx-ingress sends " +
            "$best_http_host), so a trusted proxy does not make the value trustworthy — and Request.Host " +
            "feeds URL generation, redirect targets, password-reset links, and cache keys. Set " +
            "AllowedHosts in configuration to the hostnames this API actually serves (for example " +
            "\"api.example.com;api.internal\"), or set ApiPipeline:ForwardedHeaders:ProcessHost to false " +
            "and let Request.Host come from the connection.";

        if (StrictEnvironment.Applies(environment))
        {
            throw new InvalidOperationException(
                $"{Message} (Environment {StrictEnvironment.Describe(environment)}: this check fails " +
                "startup in every environment except Development.)");
        }

        logger.ForwardedHeadersMisconfigured(Message);
    }

    /// <summary>The <c>AllowedHosts</c> list when it is real (non-empty, no <c>*</c>); null otherwise.</summary>
    private static List<string>? HostAllowList(IEnumerable<string>? allowedHosts)
    {
        var hosts = (allowedHosts ?? [])
            .Select(h => h?.Trim())
            .Where(h => !string.IsNullOrEmpty(h))
            .Select(h => h!)
            .ToList();

        return hosts.Count > 0 && !hosts.Contains("*", StringComparer.Ordinal) ? hosts : null;
    }

    private static void Report(
        ApiPipelineForwardedHeadersOptions settings,
        IHostEnvironment environment,
        ILogger logger,
        string message)
    {
        if (StrictEnvironment.Applies(environment) && settings.EnforceTrustedProxyConfiguration)
        {
            throw new InvalidOperationException(
                $"{message} (Environment {StrictEnvironment.Describe(environment)}: this check fails " +
                "startup in every environment except Development.)");
        }

        logger.ForwardedHeadersMisconfigured(message);
    }
}
