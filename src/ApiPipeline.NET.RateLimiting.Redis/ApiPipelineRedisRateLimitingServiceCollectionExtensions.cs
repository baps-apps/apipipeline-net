using ApiPipeline.NET.Configuration;
using ApiPipeline.NET;
using ApiPipeline.NET.RateLimiting;
using ApiPipeline.NET.RateLimiting.Redis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Registers shared (Redis) rate limiting: every <c>ApiPipeline:RateLimiting</c> policy is enforced
/// across replicas.
/// </summary>
/// <remarks>
/// Call with <c>AddApiPipeline</c>, in either order. Off until
/// <c>ApiPipeline:RateLimiting:Redis:Enabled</c> is <c>true</c>. Idempotent.
/// </remarks>
public static class ApiPipelineRedisRateLimitingServiceCollectionExtensions
{
    /// <summary>
    /// Shares rate limit policies through Redis, configured from the
    /// <c>ApiPipeline:RateLimiting:Redis</c> section.
    /// </summary>
    /// <param name="services">The service collection to add services to.</param>
    /// <param name="configuration">The application configuration, or the pipeline section.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="configuration"/> is the feature's own section (<c>RateLimiting</c> or <c>RateLimiting:Redis</c>) rather than the root or the
    /// pipeline section.
    /// </exception>
    /// <returns>The same <see cref="IServiceCollection"/> instance to enable fluent chaining.</returns>
    /// <example>
    /// <code>
    /// builder.Services.AddApiPipeline(builder.Configuration);
    /// builder.Services.AddApiPipelineRedisRateLimiting(builder.Configuration);
    /// </code>
    /// </example>
    public static IServiceCollection AddApiPipelineRedisRateLimiting(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        return services.AddRedisRateLimiting(configuration, configure: null, connection: null);
    }

    /// <summary>Shares rate limit policies through Redis, configured in code.</summary>
    /// <param name="services">The service collection to add services to.</param>
    /// <param name="configure">Configures the options.</param>
    /// <returns>The same <see cref="IServiceCollection"/> instance to enable fluent chaining.</returns>
    public static IServiceCollection AddApiPipelineRedisRateLimiting(
        this IServiceCollection services,
        Action<ApiPipelineRedisRateLimitingOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        return services.AddRedisRateLimiting(configuration: null, configure, connection: null);
    }

    /// <summary>
    /// Shares rate limit policies through Redis over a connection supplied in code, configured from
    /// the <c>ApiPipeline:RateLimiting:Redis</c> section.
    /// </summary>
    /// <param name="services">The service collection to add services to.</param>
    /// <param name="configuration">The application configuration, or the pipeline section.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="configuration"/> is the feature's own section (<c>RateLimiting</c> or <c>RateLimiting:Redis</c>) rather than the root or the
    /// pipeline section.
    /// </exception>
    /// <param name="connection">
    /// Returns the connection; wins over <c>Configuration</c> and a registered
    /// <see cref="IConnectionMultiplexer"/>. Retried until it returns one. Not disposed by the
    /// package. The first factory registered wins.
    /// </param>
    /// <returns>The same <see cref="IServiceCollection"/> instance to enable fluent chaining.</returns>
    /// <example>
    /// <code>
    /// builder.Services.AddApiPipelineRedisRateLimiting(
    ///     builder.Configuration,
    ///     sp => sp.GetRequiredService&lt;MyRedis&gt;().Multiplexer);
    /// </code>
    /// </example>
    public static IServiceCollection AddApiPipelineRedisRateLimiting(
        this IServiceCollection services,
        IConfiguration configuration,
        Func<IServiceProvider, IConnectionMultiplexer> connection)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(connection);

        return services.AddRedisRateLimiting(configuration, configure: null, connection);
    }

    private static IServiceCollection AddRedisRateLimiting(
        this IServiceCollection services,
        IConfiguration? configuration,
        Action<ApiPipelineRedisRateLimitingOptions>? configure,
        Func<IServiceProvider, IConnectionMultiplexer>? connection)
    {
        // Before anything is registered, so a rejected call leaves the collection untouched.
        if (configuration is IConfigurationSection passed)
        {
            PipelineSection.ThrowIfFeatureSection(
                passed,
                $"{ApiPipelineConfigurationKeys.RateLimiting}:{RedisRateLimitingConfigurationKeys.Redis}",
                ApiPipelineConfigurationKeys.Section,
                ApiPipelineConfigurationKeys.RateLimiting,
                RedisRateLimitingConfigurationKeys.Redis);
        }

        var options = services.AddOptions<ApiPipelineRedisRateLimitingOptions>();

        if (services.TryClaim<FeatureClaim>())
        {
            services.TryAddEnumerable(ServiceDescriptor.Singleton<
                IValidateOptions<ApiPipelineRedisRateLimitingOptions>, ApiPipelineRedisRateLimitingOptionsValidator>());
            options.ValidateOnStart();
            services.TryAddEnumerable(ServiceDescriptor.Singleton<
                IValidateOptions<ApiPipelineRateLimitingOptions>, QueueLimitValidator>());

            services.AddLogging();
            services.AddMetrics();
            services.TryAddSingleton(TimeProvider.System);
            services.TryAddSingleton<RedisConnectionSource>();
            services.TryAddSingleton<RedisCircuit>();
            services.TryAddSingleton<RedisRateLimitingMetrics>();
            // Not AddHttpContextAccessor: an async-local write per request for one diagnostic.
            services.TryAddSingleton<SynchronousAttemptMonitor>();
            services.TryAddSingleton<RedisServerCheck>();
            services.AddApiPipelineStartupCheck<RedisRateLimitingStartupCheck>();
        }

        if (configuration is not null)
        {
            IConfiguration pipeline = configuration as IConfigurationSection
                ?? configuration.GetSection(ApiPipelineConfigurationKeys.Section);
            var section = pipeline
                .GetSection(ApiPipelineConfigurationKeys.RateLimiting)
                .GetSection(RedisRateLimitingConfigurationKeys.Redis);

            if (services.ClaimBinding<ApiPipelineRedisRateLimitingOptions>(section))
            {
                options.Bind(section).ValidateDurations(
                    section,
                    new DurationKey("CommandTimeout", "CommandTimeoutMilliseconds"),
                    new DurationKey("FallbackCooldown", "FallbackCooldownSeconds"),
                    new DurationKey("SamplingDuration", "SamplingDurationSeconds"),
                    new DurationKey("ConcurrencyLeaseTimeout", "ConcurrencyLeaseTimeoutSeconds"),
                    new DurationKey("ConcurrencyLeaseMaxDuration", FormerName: null));
            }
        }

        if (configure is not null)
        {
            options.Configure(configure);
        }

        if (connection is not null)
        {
            services.TryAddSingleton(new ExplicitRedisConnection(connection));
        }

        // Replace, so it wins over the core TryAdd in any order; never an application's factory.
        ThrowIfCustomFactoryRegistered(services);
        services.Replace(ServiceDescriptor.Singleton<IApiPipelineRateLimiterFactory, RedisRateLimiterFactory>());
        return services;
    }

    private static void ThrowIfCustomFactoryRegistered(IServiceCollection services)
    {
        foreach (var descriptor in services)
        {
            if (descriptor.ServiceType != typeof(IApiPipelineRateLimiterFactory) || descriptor.IsKeyedService)
            {
                continue;
            }

            var implementation = descriptor.ImplementationType ?? descriptor.ImplementationInstance?.GetType();
            if (implementation == typeof(InMemoryRateLimiterFactory) || implementation == typeof(RedisRateLimiterFactory))
            {
                continue;
            }

            throw new InvalidOperationException(RedisRateLimitingStartupCheck.CustomFactoryMessage(
                implementation?.FullName ?? "registered by a delegate",
                "before"));
        }
    }

    private sealed class FeatureClaim;
}
