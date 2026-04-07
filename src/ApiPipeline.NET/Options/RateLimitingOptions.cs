using System.ComponentModel.DataAnnotations;
using System.Threading.RateLimiting;

namespace ApiPipeline.NET.Options;

/// <summary>
/// Controls rate-limiting behaviour when the client IP address cannot be determined
/// (e.g. <c>RemoteIpAddress</c> is null because forwarded headers are misconfigured).
/// </summary>
public enum AnonymousFallbackBehavior
{
    /// <summary>
    /// Reject the request with HTTP 429 immediately. Safe default — prevents
    /// a single unknown client from exhausting a shared "anonymous" bucket.
    /// </summary>
    Reject,

    /// <summary>
    /// Apply rate limiting using a single shared "anonymous" bucket.
    /// Warning: one client can exhaust this bucket for all anonymous traffic.
    /// </summary>
    RateLimit,

    /// <summary>
    /// Skip rate limiting for requests with no determinable IP.
    /// Only use when you have an alternate enforcement mechanism upstream.
    /// </summary>
    Allow
}

/// <summary>
/// Configuration options for global and named ASP.NET Core rate limiting policies.
/// Validation is conditional: when <see cref="Enabled"/> is false, policy configuration is not required.
/// </summary>
public sealed class RateLimitingOptions : IValidatableObject
{
    /// <summary>
    /// Indicates whether rate limiting is enabled for the application.
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// The name of the policy applied when no explicit policy is selected.
    /// </summary>
    public string DefaultPolicy { get; set; } = "strict";

    /// <summary>
    /// The set of named rate limiting policies that can be applied to requests.
    /// </summary>
    public List<RateLimitPolicy> Policies { get; set; } = new();

    /// <summary>
    /// Controls what happens when <c>RemoteIpAddress</c> is null (e.g. misconfigured forwarded headers).
    /// Defaults to <see cref="AnonymousFallbackBehavior.Reject"/> to prevent shared-bucket exhaustion.
    /// </summary>
    public AnonymousFallbackBehavior AnonymousFallback { get; set; } = AnonymousFallbackBehavior.Reject;

    /// <summary>
    /// When <c>true</c>, adds <c>X-RateLimit-Limit</c> and <c>X-RateLimit-Reset</c> headers
    /// to all rate-limited responses, enabling client-side adaptive backoff.
    /// </summary>
    public bool EmitRateLimitHeaders { get; set; } = true;

    /// <summary>
    /// When <c>true</c>, requests carrying the <see cref="ApiKeyHeader"/> header are partitioned
    /// by API key for rate limiting, providing per-client SLAs for machine-to-machine traffic
    /// without requiring full authentication infrastructure.
    /// </summary>
    public bool EnableApiKeyPartitioning { get; set; } = false;

    /// <summary>
    /// The header name used for API key partitioning. Only used when
    /// <see cref="EnableApiKeyPartitioning"/> is <c>true</c>.
    /// </summary>
    public string ApiKeyHeader { get; set; } = "X-Api-Key";

    /// <inheritdoc />
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!Enabled)
        {
            yield break;
        }

        if (string.IsNullOrWhiteSpace(DefaultPolicy))
        {
            yield return new ValidationResult(
                "DefaultPolicy is required when rate limiting is enabled.",
                [nameof(DefaultPolicy)]);
        }

        if (Policies.Count == 0)
        {
            yield return new ValidationResult(
                "At least one policy must be configured when rate limiting is enabled.",
                [nameof(Policies)]);
        }

        if (!string.IsNullOrWhiteSpace(DefaultPolicy)
            && Policies.Count > 0
            && !Policies.Any(p => string.Equals(p.Name, DefaultPolicy, StringComparison.OrdinalIgnoreCase)))
        {
            yield return new ValidationResult(
                $"DefaultPolicy '{DefaultPolicy}' does not match any configured policy name.",
                [nameof(DefaultPolicy)]);
        }

        var duplicateNames = Policies
            .Where(p => !string.IsNullOrWhiteSpace(p.Name))
            .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        foreach (var dup in duplicateNames)
        {
            yield return new ValidationResult(
                $"Duplicate policy name '{dup}'.",
                [nameof(Policies)]);
        }

        for (var i = 0; i < Policies.Count; i++)
        {
            foreach (var result in Policies[i].ValidatePolicy(i))
            {
                yield return result;
            }
        }
    }
}

/// <summary>
/// The underlying limiter type used for a rate limiting policy.
/// </summary>
public enum RateLimiterKind
{
    /// <summary>
    /// Uses a fixed time window limiter.
    /// </summary>
    FixedWindow,

    /// <summary>
    /// Uses a sliding time window limiter.
    /// </summary>
    SlidingWindow,

    /// <summary>
    /// Uses a concurrency-based limiter.
    /// </summary>
    Concurrency,

    /// <summary>
    /// Uses a token bucket limiter. Allows controlled bursts while enforcing a sustained rate.
    /// Requires <see cref="RateLimitPolicy.TokensPerPeriod"/> and <see cref="RateLimitPolicy.WindowSeconds"/>.
    /// </summary>
    TokenBucket
}

/// <summary>
/// Describes a single named rate limiting policy.
/// </summary>
public sealed class RateLimitPolicy
{
    /// <summary>
    /// The unique name of the policy.
    /// </summary>
    [Required, MinLength(1)]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// The limiter kind used to enforce the policy.
    /// </summary>
    [Required]
    public RateLimiterKind Kind { get; set; } = RateLimiterKind.FixedWindow;

    /// <summary>
    /// The maximum number of permits allowed within the configured window or concurrency scope.
    /// </summary>
    [Range(1, int.MaxValue)]
    public int PermitLimit { get; set; }

    /// <summary>
    /// The maximum number of queued requests waiting for permits.
    /// </summary>
    [Range(0, int.MaxValue)]
    public int QueueLimit { get; set; }

    /// <summary>
    /// The length of the rate limiting window, in seconds. Required for time-window based limiters.
    /// </summary>
    [Range(1, int.MaxValue)]
    public int? WindowSeconds { get; set; }

    /// <summary>
    /// The number of segments in a sliding window. Required for sliding window limiters.
    /// </summary>
    [Range(1, int.MaxValue)]
    public int? SegmentsPerWindow { get; set; }

    /// <summary>
    /// Determines how queued requests are processed.
    /// </summary>
    [Required]
    public QueueProcessingOrder QueueProcessingOrder { get; set; } = QueueProcessingOrder.OldestFirst;

    /// <summary>
    /// Indicates whether permits are automatically replenished at the end of each window.
    /// </summary>
    public bool AutoReplenishment { get; set; } = true;

    /// <summary>
    /// Token replenishment rate (permits per <see cref="WindowSeconds"/>). Required for <see cref="RateLimiterKind.TokenBucket"/>.
    /// </summary>
    [Range(1, int.MaxValue)]
    public int? TokensPerPeriod { get; set; }

    internal IEnumerable<ValidationResult> ValidatePolicy(int index)
    {
        var prefix = $"Policies[{index}] '{Name}'";

        if (Kind is RateLimiterKind.FixedWindow or RateLimiterKind.SlidingWindow or RateLimiterKind.TokenBucket
            && WindowSeconds is null)
        {
            yield return new ValidationResult(
                $"{prefix}: WindowSeconds is required for {Kind} policies.",
                [nameof(WindowSeconds)]);
        }

        if (Kind is RateLimiterKind.SlidingWindow && SegmentsPerWindow is null)
        {
            yield return new ValidationResult(
                $"{prefix}: SegmentsPerWindow is required for SlidingWindow policies.",
                [nameof(SegmentsPerWindow)]);
        }

        if (Kind is RateLimiterKind.TokenBucket && TokensPerPeriod is null)
        {
            yield return new ValidationResult(
                $"{prefix}: TokensPerPeriod is required for TokenBucket policies.",
                [nameof(TokensPerPeriod)]);
        }
    }
}
