using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace ApiPipeline.NET.RateLimiting.Redis;

/// <summary>
/// Fails startup when an application factory replaced the Redis one, or, with sharing on, when there
/// is no connection source, or <c>KeyPrefix</c> or <c>PartitionKeySecret</c> is unset outside
/// Development. Then starts connecting without waiting.
/// </summary>
internal sealed class RedisRateLimitingStartupCheck : IApiPipelineStartupCheck
{
    private readonly IOptions<ApiPipelineRedisRateLimitingOptions> _options;
    private readonly RedisConnectionSource _connection;
    private readonly IHostEnvironment _environment;
    private readonly IServiceScopeFactory _scopes;
    private readonly IOptions<ApiPipelineRateLimitingOptions> _rateLimiting;

    /// <summary>The conflict with an application factory registered <paramref name="when"/> the add-on's.</summary>
    /// <param name="factory">The factory type.</param>
    /// <param name="when"><c>before</c> or <c>after</c>.</param>
    internal static string CustomFactoryMessage(string factory, string when) =>
        "AddApiPipelineRedisRateLimiting supplies the IApiPipelineRateLimiterFactory that backs every rate " +
        $"limit policy, but this application also registers its own ({factory}), {when} that call. Only one factory " +
        "can back the policies, and the other would be silently ignored. To share limits through Redis, " +
        "remove your IApiPipelineRateLimiterFactory registration; to keep your factory, do not call " +
        "AddApiPipelineRedisRateLimiting.";

    internal const string KeyPrefixRequiredMessage =
        "ApiPipeline:RateLimiting:Redis:KeyPrefix is required outside Development. Every API that shares " +
        "a Redis needs its own prefix, or two APIs with the same policy name and limits share buckets; the " +
        "fallback, {ApplicationName}:{EnvironmentName}, is the assembly name, which is often generic. Set " +
        "it to a value unique to this API and environment, for example 'orders-api:prod'.";

    internal const string PartitionKeySecretRequiredMessage =
        "ApiPipeline:RateLimiting:Redis:PartitionKeySecret is required outside Development. Partition keys " +
        "hold user ids and client addresses, and without a secret their Redis keys can be reversed by " +
        "anyone who can list them (every IPv4 address can be hashed in minutes). Set it to the same random " +
        "value of at least 32 characters on every pod, from a secret store.";

    public RedisRateLimitingStartupCheck(
        IOptions<ApiPipelineRedisRateLimitingOptions> options,
        RedisConnectionSource connection,
        IHostEnvironment environment,
        IServiceScopeFactory scopes,
        IOptions<ApiPipelineRateLimitingOptions> rateLimiting)
    {
        _options = options;
        _connection = connection;
        _environment = environment;
        _scopes = scopes;
        _rateLimiting = rateLimiting;
    }

    public void Run()
    {
        ThrowIfFactoryReplaced();

        if (!_options.Value.Enabled)
        {
            return;
        }

        // The same rule as the core package's guards: only Development is relaxed.
        if (_options.Value.KeyPrefix is null && !_environment.IsDevelopment())
        {
            throw new InvalidOperationException(
                $"{KeyPrefixRequiredMessage} (Environment '{_environment.EnvironmentName}'.)");
        }

        if (_options.Value.PartitionKeySecret is null && !_environment.IsDevelopment())
        {
            throw new InvalidOperationException(
                $"{PartitionKeySecretRequiredMessage} (Environment '{_environment.EnvironmentName}'.)");
        }

        if (!_connection.HasConnectionSource)
        {
            throw new InvalidOperationException(RedisConnectionSource.NoSourceMessage);
        }

        // Nothing to share while the core rate limiter is off: the settings above are still checked
        // (they are wrong either way), but no connection is opened.
        if (!_rateLimiting.Value.Enabled)
        {
            return;
        }

        _connection.StartConnecting();
    }

    // A factory registered after the add-on's wins resolution. Resolved in a scope, so one the
    // application registered scoped is still reported by name.
    private void ThrowIfFactoryReplaced()
    {
        using var scope = _scopes.CreateScope();
        var factory = scope.ServiceProvider.GetService<IApiPipelineRateLimiterFactory>();
        if (factory is not null and not RedisRateLimiterFactory)
        {
            throw new InvalidOperationException(CustomFactoryMessage(factory.GetType().FullName!, "after"));
        }
    }
}
