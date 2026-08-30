// Shared source, compiled into every package (src/Shared/Shared.props).
using Microsoft.Extensions.Configuration;

namespace ApiPipeline.NET.Shared;

/// <summary>
/// The rule every <c>AddApiPipeline*(IConfiguration)</c> method applies to the configuration passed:
/// the application configuration (its pipeline section is read) or the pipeline section itself,
/// never a feature's own section.
/// </summary>
internal static class PipelineSection
{
    /// <summary>
    /// Throws when <paramref name="configuration"/> is one of the feature's own sections (key in
    /// <paramref name="featureKeys"/>, ignoring case): read as the pipeline section, it would bind
    /// nothing and leave the feature at its defaults.
    /// </summary>
    /// <param name="configuration">The section passed in; named as the public methods' parameter, which the exception reports.</param>
    /// <param name="settingsPath">Where the feature's settings sit under the pipeline section (<c>Cors</c>, <c>RateLimiting:Redis</c>).</param>
    /// <param name="pipelineSectionName">The pipeline section's name in the application configuration.</param>
    /// <param name="featureKeys">Section names that are the feature's own (every segment of <paramref name="settingsPath"/>).</param>
    /// <exception cref="ArgumentException">The section is the feature's own.</exception>
    public static void ThrowIfFeatureSection(
        IConfigurationSection configuration,
        string settingsPath,
        string pipelineSectionName,
        params string[] featureKeys)
    {
        foreach (var key in featureKeys)
        {
            if (string.Equals(configuration.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    $"The configuration passed is the '{configuration.Path}' section itself. A section is read as " +
                    $"the pipeline section, so the settings would be read from '{configuration.Path}:{settingsPath}', " +
                    "which binds nothing and leaves the feature at its defaults. Pass the root configuration " +
                    $"(builder.Configuration) or the pipeline section (configuration.GetSection(\"{pipelineSectionName}\")).",
                    nameof(configuration));
            }
        }
    }
}
