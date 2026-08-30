using System.ComponentModel.DataAnnotations;

namespace ApiPipeline.NET;

/// <summary>Response compression settings, section <c>ApiPipeline:ResponseCompression</c>.</summary>
public sealed class ApiPipelineResponseCompressionOptions : IValidatableObject
{
    /// <summary>Whether responses are compressed. Default <c>false</c>.</summary>
    public bool Enabled { get; set; }

    /// <summary>Compresses HTTPS responses. Default <c>false</c>: exposes secrets mixed with caller input to BREACH.</summary>
    public bool EnableForHttps { get; set; }

    /// <summary>Offers Brotli. Default <c>true</c>.</summary>
    public bool EnableBrotli { get; set; } = true;

    /// <summary>Offers Gzip. Default <c>true</c>.</summary>
    public bool EnableGzip { get; set; } = true;

    /// <summary>MIME types to compress. Unset or empty: the framework's list plus <c>application/problem+json</c>.</summary>
    public IList<string>? MimeTypes { get; set; }

    /// <summary>MIME types never compressed.</summary>
    public IList<string>? ExcludedMimeTypes { get; set; }

    /// <summary>Paths exempt when <see cref="ExcludedPaths"/> is not configured: <c>/health</c> and <c>/internal/health</c>.</summary>
    public static IReadOnlyList<string> DefaultExcludedPaths { get; } = ProbePaths.Default;

    /// <summary>Paths never compressed. Unset uses <see cref="DefaultExcludedPaths"/>; a configured list, even empty, replaces it.</summary>
    public IList<string>? ExcludedPaths { get; set; }

    /// <inheritdoc />
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext) =>
        ProbePaths.Validate(ExcludedPaths, nameof(ExcludedPaths));
}
