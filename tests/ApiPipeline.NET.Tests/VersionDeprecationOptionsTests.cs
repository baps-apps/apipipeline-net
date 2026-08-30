using System.ComponentModel.DataAnnotations;
using ApiPipeline.NET.Versioning;
using FluentAssertions;
using Microsoft.Extensions.Options;

namespace ApiPipeline.NET.Tests;

/// <summary>
/// Validation of the deprecation section: an unparseable version or a sunset link without a date
/// fails startup instead of silently doing nothing.
/// </summary>
public sealed class VersionDeprecationOptionsTests
{
    private static IList<ValidationResult> Validate(ApiPipelineVersionDeprecationOptions options)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(options, new ValidationContext(options), results, validateAllProperties: true);
        return results;
    }

    /// <summary>A version string the framework cannot parse must be rejected at startup.</summary>
    [Fact]
    public void UnparseableVersion_IsRejected()
    {
        var options = new ApiPipelineVersionDeprecationOptions
        {
            Enabled = true,
            DeprecatedVersions =
            [
                new DeprecatedVersion
                {
                    Version = "not-a-version",
                    DeprecationDate = DateTimeOffset.UnixEpoch,
                },
            ],
        };

        Validate(options).Should().ContainSingle()
            .Which.ErrorMessage.Should().Contain("not-a-version");
    }

    /// <summary>
    /// RFC 9745 defines the Deprecation field value as a Structured Fields Date and provides no
    /// dateless form, so an entry without a date cannot produce a conformant header.
    /// </summary>
    [Fact]
    public void MissingDeprecationDate_IsRejected()
    {
        var options = new ApiPipelineVersionDeprecationOptions
        {
            Enabled = true,
            DeprecatedVersions = [new DeprecatedVersion { Version = "1.0" }],
        };

        Validate(options).Should().ContainSingle()
            .Which.ErrorMessage.Should().Contain("DeprecationDate");
    }

    /// <summary>A sunset link with no sunset date advertises a date the response cannot carry.</summary>
    [Fact]
    public void SunsetLinkWithoutSunsetDate_IsRejected()
    {
        var options = new ApiPipelineVersionDeprecationOptions
        {
            Enabled = true,
            DeprecatedVersions =
            [
                new DeprecatedVersion
                {
                    Version = "1.0",
                    DeprecationDate = DateTimeOffset.UnixEpoch,
                    SunsetLink = "https://docs.example.com/v1",
                },
            ],
        };

        Validate(options).Should().ContainSingle()
            .Which.ErrorMessage.Should().Contain("SunsetDate");
    }

    /// <summary>
    /// A version is switched off after it is deprecated. A sunset before the deprecation means one of
    /// the dates is wrong, and clients would be told the version is gone before it was deprecated.
    /// </summary>
    [Fact]
    public void SunsetBeforeDeprecation_IsRejected()
    {
        var options = new ApiPipelineVersionDeprecationOptions
        {
            Enabled = true,
            DeprecatedVersions =
            [
                new DeprecatedVersion
                {
                    Version = "1.0",
                    DeprecationDate = DateTimeOffset.UnixEpoch.AddYears(1),
                    SunsetDate = DateTimeOffset.UnixEpoch,
                },
            ],
        };

        Validate(options).Should().ContainSingle()
            .Which.ErrorMessage.Should().Contain("SunsetDate").And.Contain("before its DeprecationDate");
    }

    /// <summary>A sunset on the deprecation date itself is allowed: the rule is only "not earlier".</summary>
    [Fact]
    public void SunsetOnTheDeprecationDate_Passes()
    {
        var options = new ApiPipelineVersionDeprecationOptions
        {
            Enabled = true,
            DeprecatedVersions =
            [
                new DeprecatedVersion { Version = "1.0", DeprecationDate = DateTimeOffset.UnixEpoch, SunsetDate = DateTimeOffset.UnixEpoch },
            ],
        };

        Validate(options).Should().BeEmpty();
    }

    /// <summary>The deprecation link is checked like the sunset link, and needs no sunset date.</summary>
    [Fact]
    public void RelativeDeprecationLink_IsRejected_AbsoluteOneNeedsNoSunset()
    {
        DeprecatedVersion Entry(string link) => new()
        {
            Version = "1.0",
            DeprecationDate = DateTimeOffset.UnixEpoch,
            DeprecationLink = link,
        };

        Validate(new ApiPipelineVersionDeprecationOptions { Enabled = true, DeprecatedVersions = [Entry("/docs/v1")] })
            .Should().ContainSingle().Which.ErrorMessage.Should().Contain("DeprecationLink");
        Validate(new ApiPipelineVersionDeprecationOptions { Enabled = true, DeprecatedVersions = [Entry("https://docs.example.com/v1")] })
            .Should().BeEmpty();
    }

    /// <summary>A relative or malformed link would produce a Link header no client can follow.</summary>
    [Fact]
    public void RelativeSunsetLink_IsRejected()
    {
        var options = new ApiPipelineVersionDeprecationOptions
        {
            Enabled = true,
            DeprecatedVersions =
            [
                new DeprecatedVersion
                {
                    Version = "1.0",
                    DeprecationDate = DateTimeOffset.UnixEpoch,
                    SunsetDate = DateTimeOffset.UnixEpoch,
                    SunsetLink = "/docs/v1",
                },
            ],
        };

        Validate(options).Should().ContainSingle()
            .Which.ErrorMessage.Should().Contain("absolute");
    }

    /// <summary>Nothing is validated when the feature is switched off.</summary>
    [Fact]
    public void Disabled_SkipsValidationEntirely()
    {
        var options = new ApiPipelineVersionDeprecationOptions
        {
            Enabled = false,
            DeprecatedVersions = [new DeprecatedVersion { Version = "not-a-version" }],
        };

        Validate(options).Should().BeEmpty();
    }

    /// <summary>
    /// The same through the validator that runs at startup (source-generated, which also checks
    /// each entry's attributes). A switched-off section with an incomplete entry, for example one
    /// waiting for its date, must not stop the host.
    /// </summary>
    [Fact]
    public void Disabled_IncompleteEntry_DoesNotFailStartupValidation()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ApiPipeline:VersionDeprecation:Enabled"] = "false",
            ["ApiPipeline:VersionDeprecation:DeprecatedVersions:0:Version"] = "1.0",
        }).Build();
        using var provider = new ServiceCollection().AddApiPipelineVersionDeprecation(configuration).BuildServiceProvider();

        var act = () => provider.GetRequiredService<IOptions<ApiPipelineVersionDeprecationOptions>>().Value;

        act.Should().NotThrow();
    }

    /// <summary>Enabled, the same entry fails startup, naming the missing date.</summary>
    [Fact]
    public void Enabled_MissingDate_FailsStartupValidation()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ApiPipeline:VersionDeprecation:Enabled"] = "true",
            ["ApiPipeline:VersionDeprecation:DeprecatedVersions:0:Version"] = "1.0",
        }).Build();
        using var provider = new ServiceCollection().AddApiPipelineVersionDeprecation(configuration).BuildServiceProvider();

        var act = () => provider.GetRequiredService<IOptions<ApiPipelineVersionDeprecationOptions>>().Value;

        act.Should().Throw<OptionsValidationException>().WithMessage("*DeprecationDate*");
    }

    /// <summary>Every spelling the framework accepts must pass.</summary>
    [Theory]
    [InlineData("1.0")]
    [InlineData("1")]
    [InlineData("2.1")]
    [InlineData("1.0-beta")]
    public void WellFormedConfiguration_Passes(string version)
    {
        var options = new ApiPipelineVersionDeprecationOptions
        {
            Enabled = true,
            DeprecatedVersions =
            [
                new DeprecatedVersion
                {
                    Version = version,
                    DeprecationDate = DateTimeOffset.UnixEpoch,
                    SunsetDate = DateTimeOffset.UnixEpoch.AddYears(1),
                    SunsetLink = "https://docs.example.com/v1",
                },
            ],
        };

        Validate(options).Should().BeEmpty();
    }
}
