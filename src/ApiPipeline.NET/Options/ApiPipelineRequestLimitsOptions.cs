using System.ComponentModel.DataAnnotations;

namespace ApiPipeline.NET;

/// <summary>
/// Kestrel, IIS, HTTP.sys and form limits, section <c>ApiPipeline:RequestLimits</c>. Unset values
/// keep the server's defaults.
/// </summary>
public sealed class ApiPipelineRequestLimitsOptions : IValidatableObject
{
    /// <summary>Whether the limits apply. Default <c>false</c>.</summary>
    public bool Enabled { get; set; }

    /// <summary>Removes <c>Server: Kestrel</c>. Default <c>true</c>; applies even when disabled.</summary>
    public bool SuppressServerHeader { get; set; } = true;

    /// <summary>Largest request body, in bytes (also the form limits). At least 1.</summary>
    [Range(1, long.MaxValue)]
    public long? MaxRequestBodySize { get; set; }

    /// <summary>Kestrel <c>MaxRequestHeadersTotalSize</c>.</summary>
    [Range(1, int.MaxValue)]
    public int? MaxRequestHeadersTotalSize { get; set; }

    /// <summary>Kestrel <c>MaxRequestHeaderCount</c>.</summary>
    [Range(1, int.MaxValue)]
    public int? MaxRequestHeaderCount { get; set; }

    /// <summary><c>FormOptions.ValueCountLimit</c>.</summary>
    [Range(1, int.MaxValue)]
    public int? MaxFormValueCount { get; set; }

    /// <summary>Kestrel <c>RequestHeadersTimeout</c> (default 30 s). 1 s to 1 h.</summary>
    public TimeSpan? RequestHeadersTimeout { get; set; }

    /// <summary>Kestrel <c>KeepAliveTimeout</c> (default 130 s). 1 s to 1 h.</summary>
    public TimeSpan? KeepAliveTimeout { get; set; }

    /// <summary>Kestrel <c>MaxConcurrentConnections</c> (default unlimited).</summary>
    [Range(1, long.MaxValue)]
    public long? MaxConcurrentConnections { get; set; }

    /// <summary>Kestrel minimum body rate (default 240 B/s). <c>0</c> turns the check off.</summary>
    [Range(0, int.MaxValue)]
    public double? MinRequestBodyDataRateBytesPerSecond { get; set; }

    /// <summary>Grace before the rate applies; 5 s when unset. Over 1 s, at most 1 h.</summary>
    public TimeSpan? MinRequestBodyDataRateGracePeriod { get; set; }

    /// <inheritdoc />
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        // Kestrel needs both halves; a grace period alone enforces nothing.
        if (MinRequestBodyDataRateGracePeriod is not null
            && MinRequestBodyDataRateBytesPerSecond is null)
        {
            yield return new ValidationResult(
                "MinRequestBodyDataRateGracePeriod was set without " +
                "MinRequestBodyDataRateBytesPerSecond. Kestrel's MinRequestBodyDataRate needs both " +
                "values, so the grace period on its own enforces nothing. Set " +
                "MinRequestBodyDataRateBytesPerSecond (or 0 to disable minimum-rate enforcement).",
                [nameof(MinRequestBodyDataRateBytesPerSecond)]);
        }

        foreach (var failure in new[]
        {
            DurationRange.Check(RequestHeadersTimeout, TimeSpan.FromSeconds(1), TimeSpan.FromHours(1), nameof(RequestHeadersTimeout)),
            DurationRange.Check(KeepAliveTimeout, TimeSpan.FromSeconds(1), TimeSpan.FromHours(1), nameof(KeepAliveTimeout)),
            DurationRange.Check(MinRequestBodyDataRateGracePeriod, TimeSpan.Zero, TimeSpan.FromHours(1), nameof(MinRequestBodyDataRateGracePeriod)),
        })
        {
            if (failure is not null)
            {
                yield return failure;
            }
        }

        // Kestrel's MinDataRate rejects a grace period of its heartbeat (1 second) or less.
        if (MinRequestBodyDataRateGracePeriod is { } gracePeriod && gracePeriod <= TimeSpan.FromSeconds(1))
        {
            yield return new ValidationResult(
                $"MinRequestBodyDataRateGracePeriod ({gracePeriod:c}) must be more than 1 second, Kestrel's " +
                "heartbeat interval.",
                [nameof(MinRequestBodyDataRateGracePeriod)]);
        }
    }
}
