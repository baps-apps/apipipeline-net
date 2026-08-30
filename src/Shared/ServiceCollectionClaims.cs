// Shared source, compiled into every package (src/Shared/Shared.props).
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ApiPipeline.NET.Shared;

/// <summary>Makes <c>Add*</c> methods safe to call more than once.</summary>
internal static class ServiceCollectionClaims
{
    /// <summary>True the first time <typeparamref name="TMarker"/> is claimed in this collection.</summary>
    /// <typeparam name="TMarker">A private marker type.</typeparam>
    /// <param name="services">The service collection.</param>
    public static bool TryClaim<TMarker>(this IServiceCollection services)
        where TMarker : class, new()
    {
        foreach (var descriptor in services)
        {
            if (descriptor.ServiceType == typeof(TMarker))
            {
                return false;
            }
        }

        services.Add(ServiceDescriptor.Singleton(new TMarker()));
        return true;
    }

    /// <summary>
    /// True the first time <typeparamref name="TOptions"/> is bound; false for the same section again
    /// (binding twice would duplicate list entries). Throws for a different section.
    /// </summary>
    /// <typeparam name="TOptions">The options type.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="section">The section about to be bound.</param>
    public static bool ClaimBinding<TOptions>(this IServiceCollection services, IConfigurationSection section)
        where TOptions : class
    {
        foreach (var descriptor in services)
        {
            if (descriptor.ServiceType == typeof(BindingClaim<TOptions>))
            {
                var bound = ((BindingClaim<TOptions>)descriptor.ImplementationInstance!).Path;
                if (string.Equals(bound, section.Path, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                throw new InvalidOperationException(
                    $"{typeof(TOptions).Name} is already bound to the configuration section '{bound}', so it " +
                    $"cannot also be bound to '{section.Path}': only one section is read, and the second would " +
                    "be ignored. Pass the same configuration (or section) to every AddApiPipeline* call, or " +
                    $"set the extra values in code with the Action<{typeof(TOptions).Name}> overload.");
            }
        }

        services.Add(ServiceDescriptor.Singleton(new BindingClaim<TOptions>(section.Path)));
        return true;
    }

    private sealed class BindingClaim<T>(string path)
        where T : class
    {
        public string Path { get; } = path;
    }
}
