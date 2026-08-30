
namespace ApiPipeline.NET.RateLimiting;

/// <summary>
/// Chooses the rate-limit partition of a request, before the built-in user → API key → IP chain:
/// per tenant, per OAuth client, and so on.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Resolvers run in registration order; the first non-null key wins.</item>
/// <item><see cref="Resolve"/> runs on every rate-limited request: synchronous, no I/O,
/// thread-safe (singleton).</item>
/// <item>A resolver that throws is skipped and logged (event 23).</item>
/// <item>Partition only on values the caller cannot choose, such as validated claims; a raw header
/// lets a caller mint a bucket per request.</item>
/// <item>The edge limiter, which runs before authentication, does not use resolvers.</item>
/// </list>
/// </remarks>
public interface IRateLimitPartitionKeyResolver
{
    /// <summary>The partition for <paramref name="context"/> under <paramref name="policy"/>, or <c>null</c>.</summary>
    /// <param name="context">The request being rate limited, after authentication.</param>
    /// <param name="policy">The policy the request is being limited by.</param>
    /// <returns>The partition, or <c>null</c> to let the next resolver or the built-in chain decide.</returns>
    RateLimitPartitionKey? Resolve(HttpContext context, ApiPipelineRateLimitPolicy policy);
}

/// <summary>A partition chosen by an <see cref="IRateLimitPartitionKeyResolver"/>.</summary>
/// <remarks>
/// <see cref="Type"/> is a metric attribute, so it must come from a small fixed set (<c>tenant</c>,
/// <c>client</c>), never from request data.
/// </remarks>
public sealed record RateLimitPartitionKey
{
    /// <summary>Creates a partition key.</summary>
    /// <param name="type">
    /// What the partition is (<c>tenant</c>, <c>client</c>): 1 to 32 characters of <c>a-z</c>,
    /// <c>0-9</c>, <c>_</c> and <c>-</c>.
    /// </param>
    /// <param name="value">The bucket within <paramref name="type"/>, for example the tenant id. Not empty.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="type"/> is not a valid type name, or <paramref name="value"/> is empty.
    /// </exception>
    public RateLimitPartitionKey(string type, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (!IsValidType(type))
        {
            throw new ArgumentException(
                $"Partition type '{type}' is not valid: use 1 to 32 characters of a-z, 0-9, '_' and '-'. " +
                "It is a metric attribute, so it must come from a small fixed set, not from the request.",
                nameof(type));
        }

        Type = type;
        Value = value;
    }

    /// <summary>What the partition is, for example <c>tenant</c>. Reported on rejection metrics.</summary>
    public string Type { get; }

    /// <summary>The bucket within <see cref="Type"/>, for example the tenant id.</summary>
    public string Value { get; }

    private static bool IsValidType(string? type)
    {
        if (string.IsNullOrEmpty(type) || type.Length > 32)
        {
            return false;
        }

        foreach (var c in type)
        {
            if (!(char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '_' or '-'))
            {
                return false;
            }
        }

        return true;
    }
}
