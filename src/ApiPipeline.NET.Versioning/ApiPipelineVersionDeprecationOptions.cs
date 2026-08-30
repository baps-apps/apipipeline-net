using System.ComponentModel.DataAnnotations;
using Asp.Versioning;
using Microsoft.Extensions.Options;

namespace ApiPipeline.NET.Versioning;

/// <summary>
/// Deprecated API versions, section <c>ApiPipeline:VersionDeprecation</c>, applied as
/// <c>Asp.Versioning</c> policies, which emit the <c>Deprecation</c>, <c>Sunset</c> and <c>Link</c>
/// headers.
/// </summary>
/// <remarks>Nothing is validated while <see cref="Enabled"/> is <c>false</c>.</remarks>
public sealed class ApiPipelineVersionDeprecationOptions : IValidatableObject
{
    /// <summary>Whether the policies are applied. Default <c>false</c>.</summary>
    public bool Enabled { get; set; }

    /// <summary>The deprecated API versions and their dates.</summary>
    public IList<DeprecatedVersion> DeprecatedVersions { get; set; } = [];

    /// <inheritdoc />
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!Enabled)
        {
            yield break;
        }

        foreach (var deprecated in DeprecatedVersions ?? [])
        {
            if (deprecated is null || string.IsNullOrWhiteSpace(deprecated.Version))
            {
                yield return new ValidationResult(
                    "A DeprecatedVersions entry has no Version. Use the same spelling as the " +
                    "[ApiVersion] attribute, for example \"1.0\".",
                    [nameof(DeprecatedVersions)]);
                continue;
            }

            // The framework's parser, so a valid value is one routing matches.
            if (!ApiVersionParser.Default.TryParse(deprecated.Version, out _))
            {
                yield return new ValidationResult(
                    $"DeprecatedVersions entry '{deprecated.Version}' is not a valid API version. " +
                    "Use the same spelling as the [ApiVersion] attribute, for example \"1.0\".",
                    [nameof(DeprecatedVersions)]);
            }

            if (deprecated.DeprecationDate is null)
            {
                yield return new ValidationResult(
                    $"DeprecatedVersions entry '{deprecated.Version}' requires a DeprecationDate. " +
                    "RFC 9745 defines the Deprecation header value as a Structured Fields Date and " +
                    "has no dateless form, so a version without a date cannot produce a conformant " +
                    "header.",
                    [nameof(DeprecatedVersions)]);
            }

            if (deprecated.DeprecationDate is { } deprecationDate
                && deprecated.SunsetDate is { } sunsetDate
                && sunsetDate < deprecationDate)
            {
                yield return new ValidationResult(
                    $"DeprecatedVersions entry '{deprecated.Version}' has a SunsetDate " +
                    $"({sunsetDate:O}) before its DeprecationDate ({deprecationDate:O}). A version " +
                    "is switched off after it is deprecated (RFC 9745: the sunset should not be earlier " +
                    "than the deprecation); one of the dates is wrong.",
                    [nameof(DeprecatedVersions)]);
            }

            if (!string.IsNullOrWhiteSpace(deprecated.DeprecationLink)
                && !Uri.IsWellFormedUriString(deprecated.DeprecationLink, UriKind.Absolute))
            {
                yield return new ValidationResult(
                    $"DeprecatedVersions entry '{deprecated.Version}' has a DeprecationLink that is " +
                    "not an absolute URI. Use a value such as " +
                    "'https://docs.example.com/v1-deprecation'.",
                    [nameof(DeprecatedVersions)]);
            }

            if (!string.IsNullOrWhiteSpace(deprecated.SunsetLink))
            {
                if (!Uri.IsWellFormedUriString(deprecated.SunsetLink, UriKind.Absolute))
                {
                    yield return new ValidationResult(
                        $"DeprecatedVersions entry '{deprecated.Version}' has a SunsetLink that is " +
                        "not an absolute URI. Use a value such as " +
                        "'https://docs.example.com/v1-sunset'.",
                        [nameof(DeprecatedVersions)]);
                }

                if (deprecated.SunsetDate is null)
                {
                    yield return new ValidationResult(
                        $"DeprecatedVersions entry '{deprecated.Version}' has a SunsetLink but no " +
                        "SunsetDate. The link advertises a sunset the response cannot state — set " +
                        "SunsetDate, or remove SunsetLink.",
                        [nameof(DeprecatedVersions)]);
                }
            }
        }
    }
}

/// <summary>One deprecated API version and its dates.</summary>
public sealed class DeprecatedVersion
{
    /// <summary>The version as in <c>[ApiVersion]</c> (<c>"1.0"</c>). Required.</summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>When the version is deprecated. Required (RFC 9745 has no dateless form).</summary>
    public DateTimeOffset? DeprecationDate { get; set; }

    /// <summary>When the version is switched off (<c>Sunset</c>, RFC 8594). Not before <see cref="DeprecationDate"/>.</summary>
    public DateTimeOffset? SunsetDate { get; set; }

    /// <summary>Absolute URL of the migration guide, sent as <c>Link; rel="deprecation"</c>.</summary>
    public string? DeprecationLink { get; set; }

    /// <summary>Absolute URL about the sunset, sent as <c>Link; rel="sunset"</c>. Requires <see cref="SunsetDate"/>.</summary>
    public string? SunsetLink { get; set; }
}

/// <summary>Source-generated validator (trimming and AOT safe).</summary>
[OptionsValidator]
internal sealed partial class ApiPipelineVersionDeprecationOptionsValidator : IValidateOptions<ApiPipelineVersionDeprecationOptions>;
