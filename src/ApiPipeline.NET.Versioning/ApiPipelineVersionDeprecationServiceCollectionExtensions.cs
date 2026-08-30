using ApiPipeline.NET.Versioning;
using Asp.Versioning;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Registers configuration-driven API version deprecation on top of <c>Asp.Versioning</c>.
/// </summary>
public static class ApiPipelineVersionDeprecationServiceCollectionExtensions
{
    /// <summary>
    /// Binds the <c>ApiPipeline:VersionDeprecation</c> configuration section and applies it to
    /// <c>Asp.Versioning</c>'s deprecation and sunset policies, so the framework emits the
    /// <c>Deprecation</c>, <c>Sunset</c> and <c>Link</c> headers.
    /// </summary>
    /// <param name="services">The service collection to add services to.</param>
    /// <param name="configuration">The application configuration, or the pipeline section.</param>
    /// <returns>The same <see cref="IServiceCollection"/> instance to enable fluent chaining.</returns>
    /// <remarks>Call before or after <c>AddApiVersioning</c>.</remarks>
    /// <exception cref="ArgumentException">
    /// <paramref name="configuration"/> is the feature's own section (<c>ApiPipeline:VersionDeprecation</c>) rather than the root or the
    /// pipeline section.
    /// </exception>
    /// <example>
    /// <code>
    /// builder.Services.AddApiVersioning(o => o.ReportApiVersions = true);
    /// builder.Services.AddApiPipelineVersionDeprecation(builder.Configuration);
    /// </code>
    /// </example>
    public static IServiceCollection AddApiPipelineVersionDeprecation(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        if (configuration is IConfigurationSection passed)
        {
            PipelineSection.ThrowIfFeatureSection(
                passed,
                VersioningConfigurationKeys.ApiVersionDeprecation,
                VersioningConfigurationKeys.Section,
                VersioningConfigurationKeys.ApiVersionDeprecation);
        }

        var options = services.AddVersioningCore();

        IConfiguration pipeline = configuration as IConfigurationSection
            ?? configuration.GetSection(VersioningConfigurationKeys.Section);
        var section = pipeline.GetSection(VersioningConfigurationKeys.ApiVersionDeprecation);

        // Bound once: twice would duplicate every entry.
        if (services.ClaimBinding<ApiPipelineVersionDeprecationOptions>(section))
        {
            options.Bind(section);

            if (configuration is not IConfigurationSection)
            {
                services.AddApiPipelineStartupCheck(sp =>
                    new LegacyVersionDeprecationSectionWarning(
                        configuration,
                        sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<LegacyVersionDeprecationSectionWarning>>()));
            }
        }

        return services;
    }

    /// <summary>
    /// Applies deprecation and sunset policies configured in code to <c>Asp.Versioning</c>.
    /// </summary>
    /// <param name="services">The service collection to add services to.</param>
    /// <param name="configure">Configures the deprecated versions.</param>
    /// <returns>The same <see cref="IServiceCollection"/> instance to enable fluent chaining.</returns>
    public static IServiceCollection AddApiPipelineVersionDeprecation(
        this IServiceCollection services,
        Action<ApiPipelineVersionDeprecationOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.AddVersioningCore().Configure(configure);
        return services;
    }

    private static OptionsBuilder<ApiPipelineVersionDeprecationOptions> AddVersioningCore(this IServiceCollection services)
    {
        var options = services.AddOptions<ApiPipelineVersionDeprecationOptions>();
        if (!services.TryClaim<FeatureClaim>())
        {
            return options;
        }

        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IValidateOptions<ApiPipelineVersionDeprecationOptions>, ApiPipelineVersionDeprecationOptionsValidator>());
        options.ValidateOnStart();

        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IConfigureOptions<ApiVersioningOptions>, ConfigureDeprecationPolicies>());

        services.AddApiPipelineStartupCheck<VersionReportingStartupWarning>();
        return options;
    }

    private sealed class FeatureClaim;
}
