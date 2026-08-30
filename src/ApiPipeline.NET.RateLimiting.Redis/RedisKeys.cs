using System.Security.Cryptography;
using System.Text;
using StackExchange.Redis;

namespace ApiPipeline.NET.RateLimiting.Redis;

/// <summary>
/// Redis key names. The partition key (user id, IP prefix, API key hash) is hashed: HMAC-SHA256 under
/// <c>PartitionKeySecret</c>, or plain SHA-256 in Development, which can be reversed for IPv4.
/// Pseudonymous, not anonymous (GDPR Art. 4(5)).
/// </summary>
internal static class RedisKeys
{
    /// <summary><c>{prefix}:apipipeline:rl:{digest}</c>; the braces are a cluster hash tag.</summary>
    public static RedisKey For(string prefix, string partitionKey, byte[]? partitionKeySecret = null)
    {
        var data = Encoding.UTF8.GetBytes(partitionKey);
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        if (partitionKeySecret is null)
        {
            SHA256.HashData(data, hash);
        }
        else
        {
            HMACSHA256.HashData(partitionKeySecret, data, hash);
        }

        return new RedisKey($"{prefix}:apipipeline:rl:{{{Convert.ToHexStringLower(hash[..16])}}}");
    }
}
