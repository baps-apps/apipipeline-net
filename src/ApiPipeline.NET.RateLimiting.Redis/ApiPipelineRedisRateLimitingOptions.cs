using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace ApiPipeline.NET.RateLimiting.Redis;

/// <summary>
/// Shares the <c>ApiPipeline:RateLimiting</c> policies across replicas through Redis. Section
/// <c>ApiPipeline:RateLimiting:Redis</c>.
/// </summary>
/// <remarks>Policies must have <c>QueueLimit</c> <c>0</c>: a queue cannot span pods.</remarks>
public sealed class ApiPipelineRedisRateLimitingOptions : IValidatableObject
{
    /// <summary>Whether policies are shared through Redis. Default <c>false</c> (per pod).</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// A StackExchange.Redis connection string; the package then owns the connection
    /// (<c>abortConnect=false</c>). A connection passed in code wins over it, and it over a registered
    /// <c>IConnectionMultiplexer</c>. Never logged; an unparseable value fails startup.
    /// </summary>
    public string? Configuration { get; set; }

    /// <summary>
    /// Prefix of every key, unique per API and environment (<c>orders-api:prod</c>). Required outside
    /// Development. No whitespace or braces (the package adds the cluster hash tag).
    /// </summary>
    public string? KeyPrefix { get; set; }

    /// <summary>
    /// HMAC-SHA256 secret for key names, at least 32 characters, the same on every pod. Required outside
    /// Development, so listed keys do not reveal addresses or user ids. Changing it resets every bucket.
    /// Never logged.
    /// </summary>
    public string? PartitionKeySecret { get; set; }

    internal const int MinimumPartitionKeySecretLength = 32;

    /// <summary>The most a limiter call waits on Redis. Default 250 ms; 50 ms to 10 s.</summary>
    public TimeSpan CommandTimeout { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>How long the open circuit keeps every partition per pod. Default 30 s; 1 s to 1 h.</summary>
    public TimeSpan FallbackCooldown { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Consecutive failed calls that open the circuit. Default <c>5</c>. Below it, a failure costs only
    /// its own request.
    /// </summary>
    [Range(1, 1000)]
    public int FailureThreshold { get; set; } = 5;

    /// <summary>
    /// Share of failed calls over <see cref="SamplingDuration"/> that opens the circuit, catching
    /// brownouts the consecutive count misses. Default <c>0.5</c>. A <c>WRONGTYPE</c> reply counts as
    /// an answer.
    /// </summary>
    [Range(0.01, 1.0)]
    public double FailureRatio { get; set; } = 0.5;

    /// <summary>Fewest calls in <see cref="SamplingDuration"/> for <see cref="FailureRatio"/> to apply. Default <c>20</c>.</summary>
    [Range(2, 100_000)]
    public int MinimumThroughput { get; set; } = 20;

    /// <summary>The period <see cref="FailureRatio"/> is measured over. Default 10 s; 1 s to 1 h.</summary>
    public TimeSpan SamplingDuration { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long a concurrency slot outlives its last renewal, which bounds a slot left by a dead pod.
    /// Default 30 s; 3 s to 1 h. Held leases renew every third of it.
    /// </summary>
    public TimeSpan ConcurrencyLeaseTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The longest a concurrency lease keeps its slot, bounding a lease never disposed. Default 1 h; up
    /// to 7 days. Raise it for long streams under a concurrency policy.
    /// </summary>
    public TimeSpan ConcurrencyLeaseMaxDuration { get; set; } = TimeSpan.FromHours(1);

    /// <summary>What a policy does while Redis cannot decide. Default <see cref="RedisFallbackBehavior.PerPod"/>.</summary>
    public RedisFallbackBehavior Fallback { get; set; } = RedisFallbackBehavior.PerPod;

    /// <summary>
    /// <see cref="Fallback"/> per policy name, for example <c>{ "login": "Reject" }</c>. Every name must
    /// match a policy.
    /// </summary>
    [SuppressMessage("Usage", "CA2227:Collection properties should be read only", Justification = "Bound from configuration, and replaceable in code like the core package's list options.")]
    public IDictionary<string, RedisFallbackBehavior> PolicyFallbacks { get; set; } =
        new Dictionary<string, RedisFallbackBehavior>(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        foreach (var failure in new[]
        {
            DurationRange.Check(CommandTimeout, TimeSpan.FromMilliseconds(50), TimeSpan.FromSeconds(10), nameof(CommandTimeout)),
            DurationRange.Check(FallbackCooldown, TimeSpan.FromSeconds(1), TimeSpan.FromHours(1), nameof(FallbackCooldown)),
            DurationRange.Check(SamplingDuration, TimeSpan.FromSeconds(1), TimeSpan.FromHours(1), nameof(SamplingDuration)),
            DurationRange.Check(ConcurrencyLeaseTimeout, TimeSpan.FromSeconds(3), TimeSpan.FromHours(1), nameof(ConcurrencyLeaseTimeout)),
            DurationRange.Check(ConcurrencyLeaseMaxDuration, ConcurrencyLeaseTimeout, TimeSpan.FromDays(7), nameof(ConcurrencyLeaseMaxDuration)),
        })
        {
            if (failure is not null)
            {
                yield return failure;
            }
        }

        // An undefined number would act as PerPod: failing open.
        if (EnumValue.Check(Fallback, nameof(Fallback)) is { } undefinedFallback)
        {
            yield return undefinedFallback;
        }

        foreach (var (policyName, behavior) in PolicyFallbacks ?? new Dictionary<string, RedisFallbackBehavior>())
        {
            if (EnumValue.Check(behavior, $"{nameof(PolicyFallbacks)}:{policyName}") is { } undefinedPolicyFallback)
            {
                yield return undefinedPolicyFallback;
            }
        }

        if (KeyPrefix is not null && (KeyPrefix.Length == 0 || KeyPrefix.Any(char.IsWhiteSpace)))
        {
            yield return new ValidationResult(
                $"KeyPrefix '{KeyPrefix}' must be non-empty and contain no whitespace, for example " +
                "'orders-api:prod'.",
                [nameof(KeyPrefix)]);
        }

        if (KeyPrefix is not null && KeyPrefix.AsSpan().ContainsAny('{', '}'))
        {
            yield return new ValidationResult(
                $"KeyPrefix '{KeyPrefix}' must not contain '{{' or '}}'. Redis Cluster hashes only the part of " +
                "a key between the first braces, so braces in the prefix would replace the package's own " +
                "hash tag and put every rate-limit key in one cluster slot.",
                [nameof(KeyPrefix)]);
        }

        if (PartitionKeySecret is not null && PartitionKeySecret.Length < MinimumPartitionKeySecretLength)
        {
            yield return new ValidationResult(
                $"PartitionKeySecret must be at least {MinimumPartitionKeySecretLength} characters; it is " +
                $"{PartitionKeySecret.Length}. A short secret can be guessed together with the address space. " +
                "Use a random value from a secret store. The value is not shown.",
                [nameof(PartitionKeySecret)]);
        }

        if (Enabled && !string.IsNullOrWhiteSpace(Configuration) && !IsValidConnectionString(Configuration))
        {
            // Neither the value nor the parser's message: either may hold the password.
            yield return new ValidationResult(
                "ApiPipeline:RateLimiting:Redis:Configuration is not a valid StackExchange.Redis " +
                "connection string (an unknown option, a value of the wrong type, or no endpoint). " +
                "The value is not shown because it may contain a password.",
                [nameof(Configuration)]);
        }
    }

    // Parsed as the connection is opened, so it cannot fail later.
    private static bool IsValidConnectionString(string configuration)
    {
        try
        {
            return ConfigurationOptions.Parse(configuration).EndPoints.Count > 0;
        }
        catch (Exception)
        {
            return false;
        }
    }
}

/// <summary>Source-generated validator (trimming and AOT safe).</summary>
[OptionsValidator]
internal sealed partial class ApiPipelineRedisRateLimitingOptionsValidator : IValidateOptions<ApiPipelineRedisRateLimitingOptions>;
