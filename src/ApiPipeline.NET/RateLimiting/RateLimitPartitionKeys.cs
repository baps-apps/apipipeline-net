using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace ApiPipeline.NET.RateLimiting;

/// <summary>
/// Rate limiter partition keys. Each ends with the policy signature, so changing a limit starts
/// fresh buckets instead of sharing them with replicas still on the old limit.
/// </summary>
internal static class RateLimitPartitionKeys
{
    /// <summary>An authenticated user; the id goes through <see cref="SafeValue"/>.</summary>
    public static string ForUser(string userId, string scope) => $"user:{SafeValue(userId)}|{scope}";

    /// <summary>A resolver's key, in its own namespace.</summary>
    public static string ForResolved(RateLimitPartitionKey key, string scope) =>
        $"custom:{key.Type}:{SafeValue(key.Value)}|{scope}";

    /// <summary>A claim; the type is length-prefixed, since URI claim types contain <c>:</c>.</summary>
    public static string ForClaim(string claimType, string value, string scope) =>
        string.Create(CultureInfo.InvariantCulture, $"claim:{claimType.Length}:{claimType}:{SafeValue(value)}|{scope}");

    /// <summary>The value when short and plain, else its hash, so no <c>|</c> or oversized value reaches the key.</summary>
    internal static string SafeValue(string value)
    {
        if (value.Length <= 128 && !value.AsSpan().ContainsAny('|', '\r', '\n'))
        {
            return value;
        }

        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(Encoding.UTF8.GetBytes(value), hash);
        return "h:" + Convert.ToHexStringLower(hash[..16]);
    }

    public static string ForIp(IPAddress address, int ipv6PrefixLength, string scope) =>
        $"ip:{NormalizeIp(address, ipv6PrefixLength)}|{scope}";

    /// <summary>An API key, hashed so the plaintext is never stored.</summary>
    public static string ForApiKey(string apiKey, string scope) => ForApiKeyHash(HashApiKey(apiKey), scope);

    public static string ForApiKeyHash(string apiKeyHash, string scope) => $"apikey:{apiKeyHash}|{scope}";

    /// <summary>32 lowercase hex characters: the first 16 bytes of SHA-256, as in <c>KnownApiKeyHashes</c>.</summary>
    public static string HashApiKey(string apiKey)
    {
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        var byteCount = Encoding.UTF8.GetByteCount(apiKey);
        byte[]? rented = null;
        var buffer = byteCount <= 256
            ? stackalloc byte[256]
            : (rented = System.Buffers.ArrayPool<byte>.Shared.Rent(byteCount)).AsSpan();

        try
        {
            var written = Encoding.UTF8.GetBytes(apiKey, buffer);
            SHA256.HashData(buffer[..written], hash);
            return Convert.ToHexStringLower(hash[..16]);
        }
        finally
        {
            if (rented is not null)
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(rented, clearArray: true);
            }
        }
    }

    /// <summary>IPv4-mapped IPv6 to IPv4; IPv6 masked to the configured prefix.</summary>
    public static string NormalizeIp(IPAddress address, int ipv6PrefixLength)
    {
        ArgumentNullException.ThrowIfNull(address);

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return address.ToString();
        }

        var prefix = Math.Clamp(ipv6PrefixLength, 1, 128);

        Span<byte> bytes = stackalloc byte[16];
        if (!address.TryWriteBytes(bytes, out var written) || written != 16)
        {
            // Unreachable for valid IPv6; never collapse unrelated clients into one bucket.
            return address.ToString();
        }

        for (var i = 0; i < 16; i++)
        {
            var bitsBefore = i * 8;
            if (bitsBefore >= prefix)
            {
                bytes[i] = 0;
            }
            else if (bitsBefore + 8 > prefix)
            {
                bytes[i] &= (byte)(0xFF << (bitsBefore + 8 - prefix));
            }
        }

        var network = new IPAddress(bytes);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{network}/{prefix}");
    }
}
