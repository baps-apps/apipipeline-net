// Shared source, compiled into every package (src/Shared/Shared.props).
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace ApiPipeline.NET.Shared;

/// <summary>A check run before the host serves traffic: throw to fail start, log to warn.</summary>
internal interface IApiPipelineStartupCheck
{
    void Run();
}

/// <summary>
/// Runs every <see cref="IApiPipelineStartupCheck"/>, in registration order, in
/// <see cref="IHostedLifecycleService.StartingAsync"/>: the host finishes every <c>StartingAsync</c>,
/// and stops on an exception, before any <c>StartAsync</c> starts the server. So a failed check
/// means the server never listens, in any registration order or with concurrent start.
/// </summary>
internal sealed class ApiPipelineStartupChecks : IHostedLifecycleService
{
    private readonly IEnumerable<IApiPipelineStartupCheck> _checks;

    public ApiPipelineStartupChecks(IEnumerable<IApiPipelineStartupCheck> checks) => _checks = checks;

    public Task StartingAsync(CancellationToken cancellationToken)
    {
        foreach (var check in _checks)
        {
            check.Run();
        }

        return Task.CompletedTask;
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

internal static class StartupCheckServiceCollectionExtensions
{
    /// <summary>Registers <typeparamref name="TCheck"/> once, and the service that runs checks.</summary>
    public static IServiceCollection AddApiPipelineStartupCheck<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TCheck>(
        this IServiceCollection services)
        where TCheck : class, IApiPipelineStartupCheck
    {
        services.AddApiPipelineStartupChecks();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IApiPipelineStartupCheck, TCheck>());
        return services;
    }

    /// <summary>Registers a check built by <paramref name="factory"/>. Not deduplicated: call it under a claim.</summary>
    public static IServiceCollection AddApiPipelineStartupCheck(
        this IServiceCollection services,
        Func<IServiceProvider, IApiPipelineStartupCheck> factory)
    {
        services.AddApiPipelineStartupChecks();
        services.Add(ServiceDescriptor.Singleton(factory));
        return services;
    }

    private static void AddApiPipelineStartupChecks(this IServiceCollection services) =>
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, ApiPipelineStartupChecks>());
}
