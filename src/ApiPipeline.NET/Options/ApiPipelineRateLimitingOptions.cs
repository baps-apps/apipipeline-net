using System.ComponentModel.DataAnnotations;
using System.Threading.RateLimiting;
using Microsoft.Extensions.Options;

namespace ApiPipeline.NET;

/// <summary>What rate limiting does with a request that has no client IP address.</summary>
public enum AnonymousFallbackBehavior
{
    /// <summary>
    /// Reject with 429 (default). On a Unix socket or named pipe this rejects every anonymous request;
    /// startup warns.
    /// </summary>
    Reject,

    /// <summary>Share one bucket; one client can exhaust it for all.</summary>
    RateLimit,

    /// <summary>Do not limit. Only with enforcement upstream.</summary>
    Allow
}

/// <summary>Rate limiting settings, section <c>ApiPipeline:RateLimiting</c>.</summary>
public sealed class ApiPipelineRateLimitingOptions : IValidatableObject
{
    /// <summary>Whether rate limiting is on. Default <c>false</c>. Policies are validated only when it is.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// The policy for endpoints that select none. A fallback, not a global limit: an endpoint with its
    /// own policy is limited by that alone.
    /// </summary>
    public string DefaultPolicy { get; set; } = "strict";

    /// <summary>
    /// A policy every rate-limited request must also pass, on top of its own or
    /// <see cref="DefaultPolicy"/>. Unset means no global ceiling. With Redis limits, each policy is a
    /// separate call.
    /// </summary>
    public string? GlobalPolicy { get; set; }

    /// <summary>The named policies. Read once at startup.</summary>
    [ValidateEnumeratedItems]
    public IList<ApiPipelineRateLimitPolicy> Policies { get; set; } = [];

    /// <summary>What to do when <c>RemoteIpAddress</c> is null. Default <see cref="AnonymousFallbackBehavior.Reject"/>.</summary>
    public AnonymousFallbackBehavior AnonymousFallback { get; set; } = AnonymousFallbackBehavior.Reject;

    /// <summary>
    /// Adds the IETF <c>RateLimit-Policy</c> field (draft -11) to successful responses. Default
    /// <c>false</c>. The draft may still change.
    /// </summary>
    public bool EmitRateLimitHeaders { get; set; }

    /// <summary>
    /// Adds <c>RateLimit-Policy</c> and <c>RateLimit</c> to 429 responses. Default <c>false</c>: they tell
    /// an unauthenticated caller the exact quota. <c>Retry-After</c> is always sent.
    /// </summary>
    public bool EmitRateLimitHeadersOnRejection { get; set; }

    /// <summary>
    /// Gives requests with an allow-listed <see cref="ApiKeyHeader"/> value their own bucket. Requires
    /// <see cref="KnownApiKeyHashes"/>. Partitioning only; it grants no access.
    /// </summary>
    public bool EnableApiKeyPartitioning { get; set; }

    /// <summary>The API key header. Default <c>X-Api-Key</c>.</summary>
    public string ApiKeyHeader { get; set; } = "X-Api-Key";

    /// <summary>
    /// Allowed keys, as 32 lowercase hex characters: the first 16 bytes of the key's SHA-256. The hash
    /// is unsalted, so use only random keys of 128 bits or more.
    /// </summary>
    public IList<string>? KnownApiKeyHashes { get; set; }

    /// <summary>
    /// Claims that choose an authenticated caller's partition, first match wins (for example
    /// <c>["tenant_id"]</c>). Read from the validated identity only. Empty keeps user → API key → IP.
    /// </summary>
    public IList<string>? PartitionClaims { get; set; }

    /// <summary>IPv6 prefix length treated as one client. Default <c>64</c>.</summary>
    [Range(1, 128)]
    public int IPv6PartitionPrefixLength { get; set; } = 64;

    /// <summary>
    /// Most address-keyed partitions kept, across policies. Default <c>100000</c>. Past it, new addresses
    /// share one overflow bucket per policy. Approximate under concurrency.
    /// </summary>
    [Range(1, int.MaxValue)]
    public int MaxIpPartitions { get; set; } = 100_000;

    /// <summary>
    /// Most identity-keyed partitions kept (resolver, claim, user, API key). Default <c>100000</c>. Past
    /// it, a new caller is limited by its address partition. Approximate under concurrency.
    /// </summary>
    [Range(1, int.MaxValue)]
    public int MaxIdentityPartitions { get; set; } = 100_000;

    /// <summary>Paths exempt when <see cref="ExcludedPaths"/> is not configured: <c>/health</c> and <c>/internal/health</c>.</summary>
    public static IReadOnlyList<string> DefaultExcludedPaths { get; } = ProbePaths.Default;

    /// <summary>
    /// Paths never rate limited, matched by segment, ignoring case. Unset uses
    /// <see cref="DefaultExcludedPaths"/>; a configured list, even empty, replaces it.
    /// </summary>
    public IList<string>? ExcludedPaths { get; set; }

    /// <summary>
    /// CIDR ranges a probe may come from. An <see cref="ExcludedPaths"/> request is exempt only from
    /// these addresses and without proxy headers. Unset uses <see cref="DefaultProbeNetworks"/>.
    /// </summary>
    public IList<string>? ProbeNetworks { get; set; }

    /// <summary>Loopback and private ranges (RFC 1918, RFC 6598, IPv6 ULA and link-local).</summary>
    public static IReadOnlyList<string> DefaultProbeNetworks { get; } = ProbePaths.DefaultNetworks;

    /// <inheritdoc />
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        // Checked even while disabled: the edge limiter reads it.
        foreach (var failure in ProbePaths.ValidateNetworks(ProbeNetworks, nameof(ProbeNetworks)))
        {
            yield return failure;
        }

        if (!Enabled)
        {
            yield break;
        }

        if (EnumValue.Check(AnonymousFallback, nameof(AnonymousFallback)) is { } undefinedFallback)
        {
            yield return undefinedFallback;
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

        if (!string.IsNullOrWhiteSpace(GlobalPolicy)
            && !Policies.Any(p => string.Equals(p.Name, GlobalPolicy, StringComparison.OrdinalIgnoreCase)))
        {
            yield return new ValidationResult(
                $"GlobalPolicy '{GlobalPolicy}' does not match any configured policy name.",
                [nameof(GlobalPolicy)]);
        }

        if (EnableApiKeyPartitioning)
        {
            var configuredHashes = (KnownApiKeyHashes ?? [])
                .Where(h => !string.IsNullOrWhiteSpace(h))
                .ToArray();

            if (configuredHashes.Length == 0)
            {
                yield return new ValidationResult(
                    "EnableApiKeyPartitioning is true but KnownApiKeyHashes is empty. The API key header is " +
                    "caller-supplied and unauthenticated, so partitioning on its raw value lets any caller mint a " +
                    "fresh rate-limit bucket per request — bypassing rate limiting completely and growing the " +
                    "limiter's partition table without bound. List the SHA-256 hashes (first 16 bytes, lowercase " +
                    "hex) of the keys that are allowed their own partition, or set EnableApiKeyPartitioning to false.",
                    [nameof(KnownApiKeyHashes)]);
            }

            foreach (var hash in configuredHashes)
            {
                if (hash.Length != 32 || !hash.All(char.IsAsciiHexDigitLower))
                {
                    yield return new ValidationResult(
                        $"KnownApiKeyHashes entry '{hash}' is not a valid key hash. Each entry must be 32 lowercase " +
                        "hex characters — the first 16 bytes of the key's SHA-256 hash.",
                        [nameof(KnownApiKeyHashes)]);
                }
            }

            if (string.IsNullOrWhiteSpace(ApiKeyHeader))
            {
                yield return new ValidationResult(
                    "ApiKeyHeader is required when EnableApiKeyPartitioning is true.",
                    [nameof(ApiKeyHeader)]);
            }
        }

        foreach (var failure in ProbePaths.Validate(ExcludedPaths, nameof(ExcludedPaths)))
        {
            yield return failure;
        }

        if (PartitionClaims is { } claims && claims.Any(string.IsNullOrWhiteSpace))
        {
            yield return new ValidationResult(
                "PartitionClaims contains an empty entry. List claim types, for example [\"tenant_id\"], " +
                "or remove the setting.",
                [nameof(PartitionClaims)]);
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

/// <summary>The limiter algorithm of a policy.</summary>
public enum RateLimiterKind
{
    /// <summary>Fixed window.</summary>
    FixedWindow,

    /// <summary>Sliding window.</summary>
    SlidingWindow,

    /// <summary>Concurrent requests.</summary>
    Concurrency,

    /// <summary>Token bucket: bursts up to <see cref="ApiPipelineRateLimitPolicy.PermitLimit"/>, refilled by <see cref="ApiPipelineRateLimitPolicy.TokensPerPeriod"/>.</summary>
    TokenBucket
}

/// <summary>A named rate limiting policy.</summary>
public sealed class ApiPipelineRateLimitPolicy
{
    /// <summary>The policy name. Required.</summary>
    [Required]
    public string Name { get; set; } = string.Empty;

    /// <summary>The limiter kind. Default <see cref="RateLimiterKind.FixedWindow"/>.</summary>
    public RateLimiterKind Kind { get; set; } = RateLimiterKind.FixedWindow;

    /// <summary>Permits per window, or concurrent requests.</summary>
    [Range(1, int.MaxValue)]
    public int PermitLimit { get; set; }

    /// <summary>Requests that may wait for a permit.</summary>
    [Range(0, int.MaxValue)]
    public int QueueLimit { get; set; }

    /// <summary>
    /// The window, or a token bucket's replenishment period, in whole seconds (<c>"00:01:00"</c>).
    /// Required except for concurrency.
    /// </summary>
    public TimeSpan? Window { get; set; }

    /// <summary><see cref="Window"/> in seconds, rounded up so a header never claims a shorter window.</summary>
    internal int? WindowInSeconds => Window is { } window ? (int)Math.Ceiling(window.TotalSeconds) : null;

    /// <summary>The <c>q</c> of <c>RateLimit-Policy</c>: a token bucket advertises its refill rate, not its burst.</summary>
    internal int AdvertisedQuota =>
        Kind is RateLimiterKind.TokenBucket && TokensPerPeriod is { } tokens ? tokens : PermitLimit;

    /// <summary><c>Retry-After</c> for a sliding window, which reports none: one segment.</summary>
    internal int? FallbackRetryAfterSeconds =>
        Kind is RateLimiterKind.SlidingWindow && Window is { } window && SegmentsPerWindow is > 0 and { } segments
            ? Math.Max(1, (int)Math.Ceiling(window.TotalSeconds / segments))
            : null;

    /// <summary>
    /// Segments in a sliding window, 1 to 1000. Required for that kind. Each request reads every
    /// segment, and with whole-second windows the cap keeps a segment at least 1 ms long. With
    /// Redis, at most 100; keep it at 10 or below, since each segment costs Redis CPU on every request.
    /// </summary>
    [Range(1, 1000)]
    public int? SegmentsPerWindow { get; set; }

    /// <summary>The order queued requests are served.</summary>
    public QueueProcessingOrder QueueProcessingOrder { get; set; } = QueueProcessingOrder.OldestFirst;

    /// <summary>Tokens added per <see cref="Window"/>. Required for a token bucket.</summary>
    [Range(1, int.MaxValue)]
    public int? TokensPerPeriod { get; set; }

    internal IEnumerable<ValidationResult> ValidatePolicy(int index)
    {
        var prefix = $"Policies[{index}] '{Name}'";

        if (EnumValue.Check(Kind, nameof(Kind), $"{prefix}: ") is { } undefinedKind)
        {
            yield return undefinedKind;
        }

        if (EnumValue.Check(QueueProcessingOrder, nameof(QueueProcessingOrder), $"{prefix}: ") is { } undefinedOrder)
        {
            yield return undefinedOrder;
        }

        if (Kind is RateLimiterKind.FixedWindow or RateLimiterKind.SlidingWindow or RateLimiterKind.TokenBucket
            && Window is null)
        {
            yield return new ValidationResult(
                $"{prefix}: Window is required for {Kind} policies.",
                [nameof(Window)]);
        }

        if (DurationRange.Check(Window, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(int.MaxValue), nameof(Window), $"{prefix}: ") is { } outOfRange)
        {
            yield return outOfRange;
        }
        else if (Window is { } window && window.Ticks % TimeSpan.TicksPerSecond != 0)
        {
            yield return new ValidationResult(
                $"{prefix}: Window ({window:c}) must be whole seconds: Retry-After and RateLimit-Policy state it in seconds.",
                [nameof(Window)]);
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
