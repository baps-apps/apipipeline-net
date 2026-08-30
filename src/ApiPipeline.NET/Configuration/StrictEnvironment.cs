using Microsoft.Extensions.Hosting;

namespace ApiPipeline.NET.Configuration;

/// <summary>
/// Where guards fail startup instead of warning: every environment but <c>Development</c>, so
/// <c>Prod</c>, <c>prd</c> or <c>Staging</c> fail closed too.
/// </summary>
internal static class StrictEnvironment
{
    public static bool Applies(IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        return !environment.IsDevelopment();
    }

    /// <summary>The environment name for guard messages.</summary>
    public static string Describe(IHostEnvironment environment) => $"'{environment.EnvironmentName}'";
}
