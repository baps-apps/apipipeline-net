using System.ComponentModel.DataAnnotations;

namespace ApiPipeline.NET;

/// <summary>
/// Output caching store settings, section <c>ApiPipeline:OutputCaching</c>, applied while
/// <see cref="Enabled"/>. Nothing is cached until an endpoint opts in.
/// </summary>
public sealed class ApiPipelineOutputCachingOptions : IValidatableObject
{
    /// <summary>Whether output caching is on. Default <c>false</c>.</summary>
    public bool Enabled { get; set; }

    /// <summary>Lifetime when a policy sets none (framework default 60 s). 1 s to 1 day.</summary>
    public TimeSpan? DefaultExpiration { get; set; }

    /// <summary>Cache size limit (framework default 100 MB).</summary>
    [Range(1, long.MaxValue)]
    public long? SizeLimitBytes { get; set; }

    /// <summary>Largest body cached (framework default 64 MB).</summary>
    [Range(1, long.MaxValue)]
    public long? MaximumBodySizeBytes { get; set; }

    /// <summary>Whether the path's case is part of the key. Default <c>false</c>.</summary>
    public bool UseCaseSensitivePaths { get; set; }

    /// <inheritdoc />
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (DurationRange.Check(DefaultExpiration, TimeSpan.FromSeconds(1), TimeSpan.FromDays(1), nameof(DefaultExpiration)) is { } failure)
        {
            yield return failure;
        }
    }
}
