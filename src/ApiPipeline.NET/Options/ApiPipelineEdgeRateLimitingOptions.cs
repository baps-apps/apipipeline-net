using System.ComponentModel.DataAnnotations;

namespace ApiPipeline.NET;

/// <summary>
/// Pre-authentication, IP-partitioned rate limiting, section <c>ApiPipeline:EdgeRateLimiting</c>.
/// Per process (N replicas allow N times the limit) and shared behind NAT, so set it generously.
/// </summary>
public sealed class ApiPipelineEdgeRateLimitingOptions : IValidatableObject
{
    /// <summary>Whether edge rate limiting is on. Default <c>false</c>.</summary>
    public bool Enabled { get; set; }

    /// <summary>Requests per <see cref="Window"/> from one address. Default <c>600</c>.</summary>
    [Range(1, int.MaxValue)]
    public int PermitLimit { get; set; } = 600;

    /// <summary>The fixed window, whole seconds. Default one minute.</summary>
    public TimeSpan Window { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Paths exempt when <see cref="ExcludedPaths"/> is not configured: <c>/health</c> and <c>/internal/health</c>.</summary>
    public static IReadOnlyList<string> DefaultExcludedPaths { get; } = ProbePaths.Default;

    /// <summary>
    /// Paths never limited, matched by segment, ignoring case. Unset uses
    /// <see cref="DefaultExcludedPaths"/>; a configured list, even empty, replaces it.
    /// </summary>
    public IList<string>? ExcludedPaths { get; set; }

    /// <summary>Most address partitions kept. Default <c>100000</c>; past it, new addresses share one bucket.</summary>
    [Range(1, int.MaxValue)]
    public int MaxIpPartitions { get; set; } = 100_000;

    /// <inheritdoc />
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (DurationRange.Check(Window, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(int.MaxValue), nameof(Window)) is { } failure)
        {
            yield return failure;
        }
        else if (Window.Ticks % TimeSpan.TicksPerSecond != 0)
        {
            yield return new ValidationResult(
                $"Window ({Window:c}) must be whole seconds: Retry-After and RateLimit-Policy state it in seconds.",
                [nameof(Window)]);
        }

        foreach (var pathFailure in ProbePaths.Validate(ExcludedPaths, nameof(ExcludedPaths)))
        {
            yield return pathFailure;
        }
    }
}
