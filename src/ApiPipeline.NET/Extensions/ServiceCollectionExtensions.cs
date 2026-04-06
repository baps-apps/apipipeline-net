using System.Diagnostics;
using System.IO.Compression;
using System.Net.Mime;
using System.Threading.RateLimiting;
using ApiPipeline.NET.Configuration;
using ApiPipeline.NET.Cors;
using ApiPipeline.NET.Middleware;
using ApiPipeline.NET.Observability;
using ApiPipeline.NET.Options;
using ApiPipeline.NET.RateLimiting;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.AspNetCore.ResponseCaching;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace ApiPipeline.NET.Extensions;

/// <summary>
/// Extension methods for registering ApiPipeline.NET features onto an <see cref="IServiceCollection"/>.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="CorrelationIdMiddleware"/> in the DI container (required for the
    /// <see cref="Microsoft.AspNetCore.Http.IMiddleware"/> activation pattern) and enables the
    /// <c>Add*</c>/<c>Use*</c> API symmetry.
    /// </summary>
    /// <param name="services">The service collection to add services to.</param>
    /// <returns>The same <see cref="IServiceCollection"/> instance to enable fluent chaining.</returns>
    public static IServiceCollection AddCorrelationId(this IServiceCollection services)
    {
        services.TryAddTransient<CorrelationIdMiddleware>();
        return services;
    }

    /// <summary>
    /// Registers ASP.NET Core rate limiting services using configuration bound to <see cref="RateLimitingOptions"/>.
    /// </summary>
    /// <param name="services">The service collection to add services to.</param>
    /// <param name="configuration">The application configuration.</param>
    /// <returns>The same <see cref="IServiceCollection"/> instance to enable fluent chaining.</returns>
    public static IServiceCollection AddRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        services.ConfigureRateLimiting(configuration);
        return services;
    }

    /// <summary>
    /// Registers response compression services using configuration bound to <see cref="ResponseCompressionSettings"/>.
    /// </summary>
    /// <param name="services">The service collection to add services to.</param>
    /// <param name="configuration">The application configuration.</param>
    /// <returns>The same <see cref="IServiceCollection"/> instance to enable fluent chaining.</returns>
    public static IServiceCollection AddResponseCompression(this IServiceCollection services, IConfiguration configuration)
    {
        services.ConfigureResponseCompression(configuration);
        return services;
    }

    /// <summary>
    /// Registers response caching services using configuration bound to <see cref="ResponseCachingSettings"/>.
    /// </summary>
    /// <param name="services">The service collection to add services to.</param>
    /// <param name="configuration">The application configuration.</param>
    /// <returns>The same <see cref="IServiceCollection"/> instance to enable fluent chaining.</returns>
    public static IServiceCollection AddResponseCaching(this IServiceCollection services, IConfiguration configuration)
    {
        services.ConfigureResponseCaching(configuration);
        return services;
    }

    /// <summary>
    /// Registers options for applying HTTP security headers.
    /// </summary>
    /// <param name="services">The service collection to add services to.</param>
    /// <param name="configuration">The application configuration.</param>
    /// <returns>The same <see cref="IServiceCollection"/> instance to enable fluent chaining.</returns>
    public static IServiceCollection AddSecurityHeaders(this IServiceCollection services, IConfiguration configuration)
    {
        services.ConfigureSecurityHeaders(configuration);
        return services;
    }

    /// <summary>
    /// Registers CORS policies using configuration bound to <see cref="CorsSettings"/>.
    /// </summary>
    /// <param name="services">The service collection to add services to.</param>
    /// <param name="configuration">The application configuration.</param>
    /// <returns>The same <see cref="IServiceCollection"/> instance to enable fluent chaining.</returns>
    public static IServiceCollection AddCors(this IServiceCollection services, IConfiguration configuration)
    {
        services.ConfigureCors(configuration);
        return services;
    }

    /// <summary>
    /// Registers options used by API version deprecation middleware.
    /// </summary>
    /// <param name="services">The service collection to add services to.</param>
    /// <param name="configuration">The application configuration.</param>
    /// <returns>The same <see cref="IServiceCollection"/> instance to enable fluent chaining.</returns>
    public static IServiceCollection AddApiVersionDeprecation(this IServiceCollection services, IConfiguration configuration)
    {
        services.ConfigureApiVersionDeprecation(configuration);
        return services;
    }

    /// <summary>
    /// Registers options for Kestrel and form request limits.
    /// </summary>
    /// <param name="services">The service collection to add services to.</param>
    /// <param name="configuration">The application configuration.</param>
    /// <returns>The same <see cref="IServiceCollection"/> instance to enable fluent chaining.</returns>
    public static IServiceCollection AddRequestLimits(this IServiceCollection services, IConfiguration configuration)
    {
        services.ConfigureRequestLimits(configuration);
        services.ConfigureFormRequestLimits();
        return services;
    }

    /// <summary>
    /// Registers forwarded headers options for reverse-proxy/load-balancer deployments.
    /// Configures trusted proxies and networks so <c>X-Forwarded-For</c>, <c>X-Forwarded-Proto</c>,
    /// and <c>X-Forwarded-Host</c> are processed correctly.
    /// </summary>
    /// <param name="services">The service collection to add services to.</param>
    /// <param name="configuration">The application configuration.</param>
    /// <returns>The same <see cref="IServiceCollection"/> instance to enable fluent chaining.</returns>
    public static IServiceCollection AddForwardedHeaders(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<ForwardedHeadersSettings>()
            .Bind(configuration.GetSection(ApiPipelineConfigurationKeys.ForwardedHeaders))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        return services;
    }

    /// <summary>
    /// Registers <see cref="Microsoft.AspNetCore.Http.IProblemDetailsService"/> with correlation ID and trace ID enrichment,
    /// enabling structured RFC 7807 error responses from <c>UseApiPipelineExceptionHandler</c>.
    /// </summary>
    /// <param name="services">The service collection to add services to.</param>
    /// <returns>The same <see cref="IServiceCollection"/> instance to enable fluent chaining.</returns>
    public static IServiceCollection AddApiPipelineExceptionHandler(this IServiceCollection services)
    {
        services.AddProblemDetails(options =>
        {
            options.CustomizeProblemDetails = context =>
            {
                context.HttpContext.Response.Headers.CacheControl = "no-store";

                context.ProblemDetails.Extensions.TryAdd(
                    "correlationId",
                    context.HttpContext.Items[CorrelationIdMiddleware.HeaderName]?.ToString());

                context.ProblemDetails.Extensions.TryAdd(
                    "traceId",
                    Activity.Current?.Id ?? context.HttpContext.TraceIdentifier);

                if (context.Exception is not null)
                {
                    ApiPipelineTelemetry.RecordExceptionHandled();
                }
            };
        });

        return services;
    }

    internal static IServiceCollection ConfigureRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<RateLimitingOptions>()
            .Bind(configuration.GetSection(ApiPipelineConfigurationKeys.RateLimiting))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddSingleton<RateLimiterPolicyResolver>();

        services.AddRateLimiter(rateLimiterOptions =>
        {
            rateLimiterOptions.OnRejected = static async (context, cancellationToken) =>
            {
                ApiPipelineTelemetry.RecordRateLimitRejected();

                var httpContext = context.HttpContext;
                var response = httpContext.Response;
                response.StatusCode = StatusCodes.Status429TooManyRequests;
                response.Headers.CacheControl = "no-store";

                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    response.Headers.RetryAfter = ((int)retryAfter.TotalSeconds).ToString();
                }

                var problemDetailsService = httpContext.RequestServices.GetRequiredService<IProblemDetailsService>();
                await problemDetailsService.WriteAsync(new ProblemDetailsContext
                {
                    HttpContext = httpContext,
                    ProblemDetails =
                    {
                        Type = "https://tools.ietf.org/html/rfc6585#section-4",
                        Title = "Too Many Requests",
                        Status = StatusCodes.Status429TooManyRequests,
                        Detail = "Rate limit exceeded. Retry after the duration indicated by the Retry-After header."
                    }
                });
            };

            rateLimiterOptions.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
            {
                var options = httpContext.RequestServices.GetRequiredService<RateLimiterPolicyResolver>().Current;
                var policy = ResolvePolicy(options, options.DefaultPolicy);
                return policy is not null
                    ? GetPartitionOrFallback(httpContext, options, policy)
                    : RateLimitPartition.GetNoLimiter("no-policy");
            });

            // Register all named policies from configuration so endpoints can call RequireRateLimiting("<policy-name>").
            var configuredOptions = configuration.GetSection(ApiPipelineConfigurationKeys.RateLimiting).Get<RateLimitingOptions>();
            if (configuredOptions is { Policies.Count: > 0 })
            {
                foreach (var configuredPolicy in configuredOptions.Policies)
                {
                    var policyName = configuredPolicy.Name;
                    if (string.IsNullOrWhiteSpace(policyName))
                    {
                        continue;
                    }

                    rateLimiterOptions.AddPolicy(policyName, httpContext =>
                    {
                        var options = httpContext.RequestServices.GetRequiredService<RateLimiterPolicyResolver>().Current;
                        var runtimePolicy = ResolvePolicy(options, policyName);
                        return runtimePolicy is not null
                            ? GetPartitionOrFallback(httpContext, options, runtimePolicy)
                            : RateLimitPartition.GetNoLimiter("no-policy");
                    });
                }
            }
        });

        return services;
    }

    private static RateLimitPartition<string> GetPartitionOrFallback(
        HttpContext context,
        RateLimitingOptions options,
        RateLimitPolicy policy)
    {
        var userId = context.User?.Identity?.IsAuthenticated == true
            ? context.User.FindFirst("sub")?.Value
              ?? context.User.FindFirst("nameid")?.Value
              ?? context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
            : null;

        if (!string.IsNullOrWhiteSpace(userId))
        {
            return CreateRateLimiterPartition($"user:{userId}", policy);
        }

        var ip = context.Connection.RemoteIpAddress?.ToString();
        if (!string.IsNullOrWhiteSpace(ip))
        {
            return CreateRateLimiterPartition($"ip:{ip}", policy);
        }

        // RemoteIpAddress is null — apply AnonymousFallback behaviour
        return options.AnonymousFallback switch
        {
            AnonymousFallbackBehavior.Reject =>
                RateLimitPartition.GetFixedWindowLimiter(
                    "ip:null:reject",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 0,
                        Window = TimeSpan.FromSeconds(1),
                        QueueLimit = 0
                    }),

            AnonymousFallbackBehavior.Allow =>
                RateLimitPartition.GetNoLimiter("ip:null:allow"),

            _ => // RateLimit — shared bucket (legacy, documented risk)
                CreateRateLimiterPartition("ip:anonymous", policy)
        };
    }

    private static RateLimitPartition<string> CreateRateLimiterPartition(string partitionKey, RateLimitPolicy policy) =>
        policy.Kind switch
        {
            RateLimiterKind.FixedWindow => RateLimitPartition.GetFixedWindowLimiter(
                partitionKey,
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = policy.PermitLimit,
                    Window = TimeSpan.FromSeconds(policy.WindowSeconds ?? 60),
                    QueueLimit = policy.QueueLimit,
                    QueueProcessingOrder = policy.QueueProcessingOrder,
                    AutoReplenishment = policy.AutoReplenishment
                }),

            RateLimiterKind.SlidingWindow => RateLimitPartition.GetSlidingWindowLimiter(
                partitionKey,
                _ => new SlidingWindowRateLimiterOptions
                {
                    PermitLimit = policy.PermitLimit,
                    Window = TimeSpan.FromSeconds(policy.WindowSeconds ?? 60),
                    SegmentsPerWindow = policy.SegmentsPerWindow ?? 4,
                    QueueLimit = policy.QueueLimit,
                    QueueProcessingOrder = policy.QueueProcessingOrder
                }),

            RateLimiterKind.Concurrency => RateLimitPartition.GetConcurrencyLimiter(
                partitionKey,
                _ => new ConcurrencyLimiterOptions
                {
                    PermitLimit = policy.PermitLimit,
                    QueueLimit = policy.QueueLimit,
                    QueueProcessingOrder = policy.QueueProcessingOrder
                }),

            RateLimiterKind.TokenBucket => RateLimitPartition.GetTokenBucketLimiter(
                partitionKey,
                _ => new TokenBucketRateLimiterOptions
                {
                    TokenLimit = policy.PermitLimit,
                    ReplenishmentPeriod = TimeSpan.FromSeconds(policy.WindowSeconds ?? 60),
                    TokensPerPeriod = policy.TokensPerPeriod ?? 1,
                    QueueLimit = policy.QueueLimit,
                    QueueProcessingOrder = policy.QueueProcessingOrder,
                    AutoReplenishment = policy.AutoReplenishment
                }),

            _ => throw new NotSupportedException($"Unsupported rate limiter kind '{policy.Kind}'.")
        };

    /// <summary>Resolves a rate limit policy by name; returns null if not found (caller should use no-op limiter).</summary>
    private static RateLimitPolicy? ResolvePolicy(RateLimitingOptions options, string policyName)
    {
        if (options.Policies is not { Count: > 0 })
        {
            return null;
        }

        return options.Policies.FirstOrDefault(p =>
            string.Equals(p.Name, policyName, StringComparison.OrdinalIgnoreCase));
    }

    internal static IServiceCollection ConfigureResponseCompression(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<ResponseCompressionSettings>()
            .Bind(configuration.GetSection(ApiPipelineConfigurationKeys.ResponseCompression))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddResponseCompression(options =>
        {
            options.EnableForHttps = true;
            options.Providers.Clear();
            options.Providers.Add<BrotliCompressionProvider>();
            options.Providers.Add<GzipCompressionProvider>();

            var baseMimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(
                [MediaTypeNames.Application.Json, "application/problem+json"]);
            options.MimeTypes = baseMimeTypes.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        });

        services.Configure<BrotliCompressionProviderOptions>(options => options.Level = CompressionLevel.Fastest);
        services.Configure<GzipCompressionProviderOptions>(options => options.Level = CompressionLevel.Fastest);

        services.TryAddSingleton<IConfigureOptions<ResponseCompressionOptions>, ConfigureResponseCompressionOptions>();

        return services;
    }

    internal static IServiceCollection ConfigureResponseCaching(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<ResponseCachingSettings>()
            .Bind(configuration.GetSection(ApiPipelineConfigurationKeys.ResponseCaching))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddResponseCaching();
        services.TryAddSingleton<IConfigureOptions<ResponseCachingOptions>, ConfigureResponseCachingOptions>();

        return services;
    }

    internal static IServiceCollection ConfigureSecurityHeaders(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<SecurityHeadersSettings>()
            .Bind(configuration.GetSection(ApiPipelineConfigurationKeys.SecurityHeaders))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        return services;
    }

    internal static IServiceCollection ConfigureCors(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<CorsSettings>()
            .Bind(configuration.GetSection(ApiPipelineConfigurationKeys.Cors))
            .ValidateDataAnnotations()
            .Validate(
                c => !c.AllowCredentials || (c.AllowedOrigins is { Length: > 0 }),
                "When AllowCredentials is true, AllowedOrigins must be configured (CORS does not allow wildcard origin with credentials).")
            .ValidateOnStart();

        // Register the live-config provider and the basic CORS services it depends on
        services.AddCors();
        services.AddSingleton<ICorsPolicyProvider, LiveConfigCorsPolicyProvider>();

        return services;
    }

    internal static IServiceCollection ConfigureApiVersionDeprecation(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<ApiVersionDeprecationOptions>()
            .Bind(configuration.GetSection(ApiPipelineConfigurationKeys.ApiVersionDeprecation))
            .ValidateDataAnnotations()
            .Validate(
                opts =>
                {
                    foreach (var dv in opts.DeprecatedVersions ?? [])
                    {
                        if (!string.IsNullOrWhiteSpace(dv.SunsetLink)
                            && !Uri.IsWellFormedUriString(dv.SunsetLink, UriKind.Absolute))
                        {
                            return false;
                        }
                    }
                    return true;
                },
                "SunsetLink must be a valid absolute URI (e.g. 'https://docs.example.com/v1-sunset') when specified.")
            .ValidateOnStart();

        return services;
    }

    internal static IServiceCollection ConfigureRequestLimits(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<RequestLimitsOptions>()
            .Bind(configuration.GetSection(ApiPipelineConfigurationKeys.RequestLimits))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // Register Kestrel configuration via validated options (replaces ConfigureKestrelRequestLimits)
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IConfigureOptions<KestrelServerOptions>, ConfigureKestrelOptions>());

        return services;
    }

    internal static IServiceCollection ConfigureFormRequestLimits(this IServiceCollection services)
    {
        services.AddOptions<FormOptions>()
            .Configure<IOptions<RequestLimitsOptions>>((options, requestLimits) =>
            {
                var limits = requestLimits.Value;
                if (!limits.Enabled)
                {
                    return;
                }

                if (limits.MaxRequestBodySize is { } maxBody)
                {
                    options.MultipartBodyLengthLimit = maxBody;
                    options.BufferBodyLengthLimit = maxBody;
                }

                if (limits.MaxFormValueCount is { } maxValueCount)
                {
                    options.ValueCountLimit = maxValueCount;
                }
            });

        return services;
    }
}
