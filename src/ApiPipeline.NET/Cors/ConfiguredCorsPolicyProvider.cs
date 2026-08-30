using ApiPipeline.NET.Correlation;
using ApiPipeline.NET.Observability;
using ApiPipeline.NET.RateLimiting;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace ApiPipeline.NET.Cors;

/// <summary>
/// Answers the default CORS lookup with the configured policy and hands every other lookup, and all
/// lookups while CORS is off, to the provider registered before it, at that provider's lifetime.
/// </summary>
internal sealed class ConfiguredCorsPolicyProvider : ICorsPolicyProvider, IDisposable, IAsyncDisposable
{
    private readonly ConfiguredCorsPolicy _configured;
    private readonly bool _ownsInner;

    public ConfiguredCorsPolicyProvider(ConfiguredCorsPolicy configured, ICorsPolicyProvider inner, bool ownsInner)
    {
        ArgumentNullException.ThrowIfNull(configured);
        ArgumentNullException.ThrowIfNull(inner);

        _configured = configured;
        Inner = inner;
        _ownsInner = ownsInner;
    }

    public ICorsPolicyProvider Inner { get; }

    public Task<CorsPolicy?> GetPolicyAsync(HttpContext context, string? policyName)
    {
        // Named lookups belong to the application; off means as if the package were absent.
        if (!string.IsNullOrEmpty(policyName) || !_configured.Enabled)
        {
            return Inner.GetPolicyAsync(context, policyName);
        }

        return _configured.DefaultPolicy;
    }

    /// <summary>Disposes the wrapped provider when this wrapper created it.</summary>
    public void Dispose()
    {
        if (_ownsInner && Inner is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    /// <inheritdoc cref="Dispose"/>
    public ValueTask DisposeAsync()
    {
        if (_ownsInner && Inner is IAsyncDisposable asyncDisposable)
        {
            return asyncDisposable.DisposeAsync();
        }

        Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Builds the wrapped provider as the container would have, compiled once at registration.
    /// Returns the provider and whether the wrapper owns it.
    /// </summary>
    internal static Func<IServiceProvider, (ICorsPolicyProvider Provider, bool Owned)> InnerFactory(ServiceDescriptor descriptor)
    {
        if (descriptor.ImplementationInstance is ICorsPolicyProvider instance)
        {
            return _ => (instance, false);
        }

        if (descriptor.ImplementationFactory is { } factory)
        {
            return services => ((ICorsPolicyProvider)factory(services), true);
        }

        var activate = ActivatorUtilities.CreateFactory(descriptor.ImplementationType!, Type.EmptyTypes);
        return services => ((ICorsPolicyProvider)activate(services, null), true);
    }

    /// <summary>
    /// The wrapped provider's lifetime, except that the stateless transient
    /// <see cref="DefaultCorsPolicyProvider"/> is wrapped once, not per request.
    /// </summary>
    internal static ServiceLifetime WrapperLifetime(ServiceDescriptor descriptor) =>
        descriptor.ImplementationType == typeof(DefaultCorsPolicyProvider) ? ServiceLifetime.Singleton : descriptor.Lifetime;
}

/// <summary>The configured default policy, built once at startup.</summary>
internal sealed class ConfiguredCorsPolicy
{
    public ConfiguredCorsPolicy(
        IOptions<ApiPipelineCorsOptions> settings,
        IHostEnvironment environment,
        ApiPipelineMetrics metrics)
    {
        var startup = settings.Value;
        Enabled = startup.Enabled;

        if (!Enabled)
        {
            DefaultPolicy = Task.FromResult<CorsPolicy?>(null);
            return;
        }

        // ValidateOnStart already built it, so this cannot throw.
        var policy = environment.IsDevelopment() && startup.AllowAllInDevelopment
            ? new CorsPolicyBuilder()
                .AllowAnyOrigin()
                .AllowAnyMethod()
                .AllowAnyHeader()
                .WithExposedHeaders(CorrelationHeaders.All)
                .WithExposedHeaders(RateLimitResponseHeaders)
                .SetPreflightMaxAgeIfSet(startup.PreflightMaxAge)
                .Build()
            : BuildConfiguredPolicy(startup, metrics);
        DefaultPolicy = Task.FromResult<CorsPolicy?>(policy);
    }

    /// <summary><c>Retry-After</c> and the rate-limit fields, exposed so browser clients can back off. A fresh array per read.</summary>
    internal static string[] RateLimitResponseHeaders =>
        [HeaderNames.RetryAfter, RateLimitFields.StatusHeader, RateLimitFields.PolicyHeader];

    public bool Enabled { get; }

    /// <summary>The policy as a completed task; <c>null</c> while disabled.</summary>
    public Task<CorsPolicy?> DefaultPolicy { get; }

    /// <summary>Builds the policy without throwing, for startup validation.</summary>
    /// <param name="settings">The options.</param>
    /// <param name="metrics">Counts rejected origins; <c>null</c> when only validating.</param>
    /// <param name="policy">The policy.</param>
    /// <param name="error">Why it could not be built.</param>
    internal static bool TryBuildConfiguredPolicy(
        ApiPipelineCorsOptions settings,
        ApiPipelineMetrics? metrics,
        out CorsPolicy policy,
        out string? error)
    {
        try
        {
            policy = BuildConfiguredPolicy(settings, metrics);
            error = null;
            return true;
        }
        catch (InvalidOperationException ex)
        {
            policy = null!;
            error = ex.Message;
            return false;
        }
        catch (ArgumentException ex)
        {
            policy = null!;
            error = ex.Message;
            return false;
        }
    }

    private static CorsPolicy BuildConfiguredPolicy(ApiPipelineCorsOptions settings, ApiPipelineMetrics? metrics)
    {
        var builder = new CorsPolicyBuilder();

        // Always exposed, or browser clients cannot read the correlation headers.
        builder.WithExposedHeaders(CorrelationHeaders.All);
        builder.WithExposedHeaders(RateLimitResponseHeaders);
        builder.SetPreflightMaxAgeIfSet(settings.PreflightMaxAge);

        var hasConfiguredOrigins = settings.AllowedOrigins is { Count: > 0 };
        if (hasConfiguredOrigins)
        {
            builder.WithOrigins([.. settings.AllowedOrigins!]);
        }
        else
        {
            builder.SetIsOriginAllowed(_ =>
            {
                metrics?.CorsRejected();
                return false;
            });
        }

        var methods = settings.AllowedMethods?
            .Where(m => !string.IsNullOrWhiteSpace(m))
            .ToArray();
        if (methods is { Length: > 0 })
        {
            if (methods.Contains("*"))
            {
                builder.AllowAnyMethod();
            }
            else
            {
                builder.WithMethods(methods);
            }
        }
        else
        {
            builder.WithMethods(ApiPipelineCorsOptions.SafeDefaultAllowedMethods);
        }

        var headers = settings.AllowedHeaders?
            .Where(h => !string.IsNullOrWhiteSpace(h))
            .ToArray();
        if (headers is { Length: > 0 })
        {
            if (headers.Contains("*"))
            {
                builder.AllowAnyHeader();
            }
            else
            {
                builder.WithHeaders(headers);
            }
        }
        else
        {
            builder.WithHeaders(ApiPipelineCorsOptions.SafeDefaultAllowedHeaders);
        }

        if (settings.AllowCredentials)
        {
            builder.AllowCredentials();
        }

        var policy = builder.Build();

        if (hasConfiguredOrigins)
        {
            // Wraps the framework's check to count rejected origins, with identical matching.
            var isAllowed = policy.IsOriginAllowed;
            policy.IsOriginAllowed = origin =>
            {
                if (isAllowed(origin))
                {
                    return true;
                }

                metrics?.CorsRejected();
                return false;
            };
        }

        return policy;
    }
}

internal static class CorsPolicyBuilderExtensions
{
    /// <summary>Sets <c>Access-Control-Max-Age</c> when <paramref name="maxAge"/> is configured.</summary>
    public static CorsPolicyBuilder SetPreflightMaxAgeIfSet(this CorsPolicyBuilder builder, TimeSpan? maxAge) =>
        maxAge is { } value ? builder.SetPreflightMaxAge(value) : builder;
}
