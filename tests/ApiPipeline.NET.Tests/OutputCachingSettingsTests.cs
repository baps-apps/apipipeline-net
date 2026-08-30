using System.Reflection;
using ApiPipeline.NET.Pipeline;
using FluentAssertions;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.Options;

namespace ApiPipeline.NET.Tests;

/// <summary>
/// Output caching is the server-side cache Microsoft recommends, so its store settings come from
/// configuration like every other feature's, and the legacy response caching entry points are
/// marked obsolete (with a diagnostic id, so a consumer can suppress just that warning).
/// </summary>
public sealed class OutputCachingSettingsTests
{
    private static async Task<OutputCacheOptions> FrameworkOptionsAsync(
        Action<Dictionary<string, string?>> configure,
        Action<IServiceCollection>? services = null)
    {
        await using var app = await TestAppBuilder.CreateAppAsync(TestAppBuilder.MinimalConfig(configure), configureServices: services);
        return app.Services.GetRequiredService<IOptions<OutputCacheOptions>>().Value;
    }

    [Fact]
    public async Task Enabled_AppliesTheStoreSettings()
    {
        var options = await FrameworkOptionsAsync(c =>
        {
            c["ApiPipeline:OutputCaching:Enabled"] = "true";
            c["ApiPipeline:OutputCaching:DefaultExpiration"] = "00:00:05";
            c["ApiPipeline:OutputCaching:SizeLimitBytes"] = "1048576";
            c["ApiPipeline:OutputCaching:MaximumBodySizeBytes"] = "65536";
            c["ApiPipeline:OutputCaching:UseCaseSensitivePaths"] = "true";
        });

        options.DefaultExpirationTimeSpan.Should().Be(TimeSpan.FromSeconds(5));
        options.SizeLimit.Should().Be(1_048_576);
        options.MaximumBodySize.Should().Be(65_536);
        options.UseCaseSensitivePaths.Should().BeTrue();
    }

    /// <summary>Unset settings keep the framework's defaults.</summary>
    [Fact]
    public async Task Enabled_UnsetSettings_KeepTheFrameworkDefaults()
    {
        var defaults = new OutputCacheOptions();

        var options = await FrameworkOptionsAsync(c => c["ApiPipeline:OutputCaching:Enabled"] = "true");

        options.DefaultExpirationTimeSpan.Should().Be(defaults.DefaultExpirationTimeSpan);
        options.SizeLimit.Should().Be(defaults.SizeLimit);
        options.MaximumBodySize.Should().Be(defaults.MaximumBodySize);
    }

    /// <summary>Off, the application's own <c>AddOutputCache(...)</c> settings are left alone.</summary>
    [Fact]
    public async Task Disabled_LeavesTheApplicationsSettingsAlone()
    {
        var options = await FrameworkOptionsAsync(
            c =>
            {
                c["ApiPipeline:OutputCaching:Enabled"] = "false";
                c["ApiPipeline:OutputCaching:DefaultExpiration"] = "00:00:05";
            },
            services => services.AddOutputCache(o => o.DefaultExpirationTimeSpan = TimeSpan.FromSeconds(42)));

        options.DefaultExpirationTimeSpan.Should().Be(TimeSpan.FromSeconds(42));
    }

    /// <summary>A bare number reads as days; startup says so instead of caching for 60 days.</summary>
    [Fact]
    public async Task DefaultExpiration_AsABareNumber_FailsStartup()
    {
        await using var app = await TestAppBuilder.CreateAppAsync(TestAppBuilder.MinimalConfig(c =>
        {
            c["ApiPipeline:OutputCaching:Enabled"] = "true";
            c["ApiPipeline:OutputCaching:DefaultExpiration"] = "60";
        }));

        var act = () => app.Services.GetRequiredService<IOptions<ApiPipelineOutputCachingOptions>>().Value;

        act.Should().Throw<OptionsValidationException>().WithMessage("*DefaultExpiration*60 days*");
    }

    [Theory]
    [InlineData("DefaultExpiration", "00:00:00")]
    [InlineData("SizeLimitBytes", "0")]
    [InlineData("MaximumBodySizeBytes", "0")]
    public async Task OutOfRangeSetting_FailsStartup(string key, string value)
    {
        await using var app = await TestAppBuilder.CreateAppAsync(TestAppBuilder.MinimalConfig(c =>
        {
            c["ApiPipeline:OutputCaching:Enabled"] = "true";
            c[$"ApiPipeline:OutputCaching:{key}"] = value;
        }));

        var act = () => app.Services.GetRequiredService<IOptions<ApiPipelineOutputCachingOptions>>().Value;

        act.Should().Throw<OptionsValidationException>().WithMessage($"*{key}*");
    }

    public static TheoryData<string> LegacyEntryPoints =>
    [
        nameof(ApiPipelineServiceCollectionExtensions.AddApiPipelineResponseCaching),
        nameof(ApiPipelineBuilder.WithResponseCaching),
    ];

    /// <summary>
    /// Response caching follows HTTP cache semantics (no authenticated responses, no tags, memory
    /// only); output caching is its replacement. The entry points warn, under their own id.
    /// </summary>
    [Theory]
    [MemberData(nameof(LegacyEntryPoints))]
    public void ResponseCachingEntryPoints_AreObsolete_WithADiagnosticId(string method)
    {
        var methods = typeof(ApiPipelineServiceCollectionExtensions).GetMethods()
            .Concat(typeof(ApiPipelineBuilder).GetMethods())
            .Where(m => m.Name == method)
            .ToList();

        methods.Should().NotBeEmpty();
        foreach (var candidate in methods)
        {
            var obsolete = candidate.GetCustomAttribute<ObsoleteAttribute>();
            obsolete.Should().NotBeNull($"{method} is the legacy cache");
            obsolete!.DiagnosticId.Should().Be("APIPIPELINE001");
            obsolete.IsError.Should().BeFalse("deprecated in 2.x, not removed");
        }
    }
}
