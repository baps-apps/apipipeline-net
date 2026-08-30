using System.Diagnostics.CodeAnalysis;
using System.IO.Compression;
using ApiPipeline.NET.Configuration;
using ApiPipeline.NET.Correlation;
using ApiPipeline.NET.Cors;
using ApiPipeline.NET.Observability;
using ApiPipeline.NET;
using ApiPipeline.NET.Pipeline;
using ApiPipeline.NET.RateLimiting;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.AspNetCore.HttpsPolicy;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.ResponseCaching;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.AspNetCore.Server.HttpSys;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers ApiPipeline.NET features.</summary>
/// <remarks>
/// Each feature has a configuration overload and an <c>Action&lt;TOptions&gt;</c> overload; both,
/// and <c>Configure&lt;TOptions&gt;</c>, feed the same validated options. Calling a method twice is
/// safe. An <c>IConfiguration</c> argument is either the application configuration (its
/// <c>ApiPipeline</c> section is read) or a section read as the pipeline section itself; a feature's
/// own section (<c>GetSection("ApiPipeline:Cors")</c>) throws <see cref="ArgumentException"/>.
/// </remarks>
public static class ApiPipelineServiceCollectionExtensions
{
    /// <summary>
    /// Registers every feature, bound from the <c>ApiPipeline</c> section. <c>UseApiPipeline</c> adds
    /// the middleware of enabled features.
    /// </summary>
    /// <remarks>
    /// Framework settings the package sets (Kestrel, HSTS, compression, caching, timeouts, forms)
    /// apply only while their feature is enabled, and only as defaults: the application's own
    /// settings win in any registration order. Kestrel's <c>Server</c> header is removed regardless,
    /// unless <c>RequestLimits:SuppressServerHeader</c> is <c>false</c>. An <c>ICorsPolicyProvider</c>
    /// registered earlier is wrapped, not replaced. See docs/ARCHITECTURE.md.
    /// </remarks>
    /// <param name="services">The service collection to add services to.</param>
    /// <param name="configuration">The application configuration, or the pipeline section.</param>
    /// <returns>The same <see cref="IServiceCollection"/> instance to enable fluent chaining.</returns>
    /// <example>
    /// <code>
    /// builder.Services.AddApiPipeline(builder.Configuration);   // reads "ApiPipeline"
    /// </code>
    /// </example>
    public static IServiceCollection AddApiPipeline(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        return services.AddAllFeatures(configuration);
    }

    /// <summary>
    /// Registers every feature, bound from the children of <paramref name="section"/>
    /// (<c>Cors</c>, <c>RateLimiting</c>, ...).
    /// </summary>
    /// <param name="services">The service collection to add services to.</param>
    /// <param name="section">The section holding the pipeline's settings.</param>
    /// <returns>The same <see cref="IServiceCollection"/> instance to enable fluent chaining.</returns>
    /// <example>
    /// <code>
    /// builder.Services.AddApiPipeline(builder.Configuration.GetSection("Platform:Api"));
    /// </code>
    /// </example>
    public static IServiceCollection AddApiPipeline(
        this IServiceCollection services,
        IConfigurationSection section)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(section);

        return services.AddAllFeatures(section);
    }

    private static IServiceCollection AddAllFeatures(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddApiPipelineCorrelationId(configuration);
        services.AddApiPipelineRateLimiting(configuration);
        services.AddApiPipelineEdgeRateLimiting(configuration);
        services.AddApiPipelineResponseCompression(configuration);
#pragma warning disable APIPIPELINE001
        services.AddApiPipelineResponseCaching(configuration);
#pragma warning restore APIPIPELINE001
        services.AddApiPipelineOutputCaching(configuration);
        services.AddApiPipelineSecurityHeaders(configuration);
        services.AddApiPipelineCors(configuration);
        services.AddApiPipelineRequestLimits(configuration);
        services.AddApiPipelineForwardedHeaders(configuration);
        services.AddApiPipelineRequestSizeTracking();
        services.AddApiPipelineRequestTimeouts(configuration);

        return services;
    }


    /// <summary>
    /// Registers correlation-id support with default <see cref="ApiPipelineCorrelationIdOptions"/>.
    /// </summary>
    /// <param name="services">The service collection to add services to.</param>
    /// <returns>The same <see cref="IServiceCollection"/> instance to enable fluent chaining.</returns>
    public static IServiceCollection AddApiPipelineCorrelationId(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services.AddFeature<ApiPipelineCorrelationIdOptions>(section: null, bind: null, configure: null, RegisterCorrelationId);
    }

    /// <summary>
    /// Registers correlation-id support, binding <see cref="ApiPipelineCorrelationIdOptions"/> from the
    /// <c>ApiPipeline:CorrelationId</c> configuration section.
    /// </summary>
    /// <param name="services">The service collection to add services to.</param>
    /// <param name="configuration">The application configuration, or the pipeline section.</param>
    /// <returns>The same <see cref="IServiceCollection"/> instance to enable fluent chaining.</returns>
    public static IServiceCollection AddApiPipelineCorrelationId(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        return services.AddFeature<ApiPipelineCorrelationIdOptions>(
            services.FeatureSection(configuration, ApiPipelineConfigurationKeys.CorrelationId),
            static (b, section) => b.Bind(section),
            configure: null,
            RegisterCorrelationId);
    }

    /// <summary>Registers correlation-id support, configured in code.</summary>
    /// <param name="services">The service collection to add services to.</param>
    /// <param name="configure">Configures the options.</param>
    /// <returns>The same <see cref="IServiceCollection"/> instance to enable fluent chaining.</returns>
    public static IServiceCollection AddApiPipelineCorrelationId(
        this IServiceCollection services,
        Action<ApiPipelineCorrelationIdOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        return services.AddFeature(section: null, bind: null, configure, RegisterCorrelationId);
    }

    /// <summary>
    /// Forwards the current request's <c>X-Correlation-Id</c> on this client's outbound requests.
    /// <c>traceparent</c> propagates on its own.
    /// </summary>
    /// <param name="builder">The HTTP client builder to attach the handler to.</param>
    /// <returns>The same <see cref="IHttpClientBuilder"/> instance to enable fluent chaining.</returns>
    /// <example>
    /// <code>
    /// builder.Services.AddHttpClient("downstream").AddApiPipelineCorrelationIdPropagation();
    /// </code>
    /// </example>
    public static IHttpClientBuilder AddApiPipelineCorrelationIdPropagation(this IHttpClientBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddHttpContextAccessor();
        builder.Services.TryAddTransient<CorrelationIdPropagationHandler>();
        builder.AddHttpMessageHandler<CorrelationIdPropagationHandler>();
        return builder;
    }

    private static void RegisterCorrelationId(
        IServiceCollection services,
        OptionsBuilder<ApiPipelineCorrelationIdOptions> options)
    {
        options.Validated<ApiPipelineCorrelationIdOptions, ApiPipelineCorrelationIdOptionsValidator>();
    }


    /// <summary>
    /// Registers ASP.NET Core rate limiting, bound from the <c>ApiPipeline:RateLimiting</c> section.
    /// </summary>
    /// <param name="services">The service collection to add services to.</param>
    /// <param name="configuration">The application configuration, or the pipeline section.</param>
    /// <returns>The same <see cref="IServiceCollection"/> instance to enable fluent chaining.</returns>
    public static IServiceCollection AddApiPipelineRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        return services
            .AddRateLimitingOptions(configuration, configure: null)
            .AddRateLimiterServices();
    }

    /// <summary>Registers ASP.NET Core rate limiting, configured in code.</summary>
    /// <param name="services">The service collection to add services to.</param>
    /// <param name="configure">Configures the options, including the named policies.</param>
    /// <returns>The same <see cref="IServiceCollection"/> instance to enable fluent chaining.</returns>
    public static IServiceCollection AddApiPipelineRateLimiting(
        this IServiceCollection services,
        Action<ApiPipelineRateLimitingOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        return services
            .AddRateLimitingOptions(configuration: null, configure)
            .AddRateLimiterServices();
    }

    /// <summary>
    /// Registers pre-authentication (edge) rate limiting, bound from the
    /// <c>ApiPipeline:EdgeRateLimiting</c> section. Shared partitioning settings come from
    /// <c>ApiPipeline:RateLimiting</c>.
    /// </summary>
    /// <param name="services">The service collection to add services to.</param>
    /// <param name="configuration">The application configuration, or the pipeline section.</param>
    /// <returns>The same <see cref="IServiceCollection"/> instance to enable fluent chaining.</returns>
    public static IServiceCollection AddApiPipelineEdgeRateLimiting(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddRateLimitingOptions(configuration, configure: null);
        return services.AddFeature<ApiPipelineEdgeRateLimitingOptions>(
            services.FeatureSection(configuration, ApiPipelineConfigurationKeys.EdgeRateLimiting),
            static (b, section) => b.Bind(section)
                .BindEmptyList(section, nameof(ApiPipelineEdgeRateLimitingOptions.ExcludedPaths), static o => o.ExcludedPaths = [])
                .ValidateDurations(
                    section,
                    new DurationKey("Window", "WindowSeconds")),
            configure: null,
            RegisterEdgeRateLimiting);
    }

    /// <summary>Registers pre-authentication (edge) rate limiting, configured in code.</summary>
    /// <param name="services">The service collection to add services to.</param>
    /// <param name="configure">Configures the options.</param>
    /// <returns>The same <see cref="IServiceCollection"/> instance to enable fluent chaining.</returns>
    public static IServiceCollection AddApiPipelineEdgeRateLimiting(
        this IServiceCollection services,
        Action<ApiPipelineEdgeRateLimitingOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.AddRateLimitingOptions(configuration: null, configure: null);
        return services.AddFeature(section: null, bind: null, configure, RegisterEdgeRateLimiting);
    }

    /// <summary>
    /// Adds a resolver asked, in registration order, before the built-in user → API key → IP chain.
    /// </summary>
    /// <typeparam name="TResolver">The resolver; a thread-safe singleton.</typeparam>
    /// <param name="services">The service collection to add services to.</param>
    /// <returns>The same <see cref="IServiceCollection"/> instance to enable fluent chaining.</returns>
    /// <example>
    /// <code>
    /// builder.Services.AddApiPipeline(builder.Configuration);
    /// builder.Services.AddApiPipelineRateLimitPartitionKeyResolver&lt;TenantPartitionKeyResolver&gt;();
    /// </code>
    /// </example>
    public static IServiceCollection AddApiPipelineRateLimitPartitionKeyResolver<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TResolver>(
        this IServiceCollection services)
        where TResolver : class, IRateLimitPartitionKeyResolver
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IRateLimitPartitionKeyResolver, TResolver>());
        return services;
    }

    private static IServiceCollection AddRateLimitingOptions(
        this IServiceCollection services,
        IConfiguration? configuration,
        Action<ApiPipelineRateLimitingOptions>? configure) =>
        services.AddFeature(
            configuration is null ? null : services.FeatureSection(configuration, ApiPipelineConfigurationKeys.RateLimiting),
            (b, section) =>
            {
                b.Bind(section)
                    .BindEmptyList(section, nameof(ApiPipelineRateLimitingOptions.ExcludedPaths), static o => o.ExcludedPaths = [])
                    .ValidateDurations(
                        section,
                        new DurationKey("Window", "WindowSeconds", Collection: "Policies"));

                var root = configuration as IConfigurationRoot;
                b.Services.AddSingleton<IValidateOptions<ApiPipelineRateLimitingOptions>>(sp =>
                    new PolicyListMergeValidator(section, root ?? sp.GetService<IConfiguration>() as IConfigurationRoot));
            },
            configure,
            static (_, options) =>
                options.Validated<ApiPipelineRateLimitingOptions, ApiPipelineRateLimitingOptionsValidator>());

    private static IServiceCollection AddRateLimiterServices(this IServiceCollection services)
    {
        if (!services.TryClaim<RateLimiterServicesClaim>())
        {
            return services;
        }

        services.AddRateLimiter(static _ => { });
        services.TryAddSingleton<IApiPipelineRateLimiterFactory, InMemoryRateLimiterFactory>();
        services.TryAddSingleton<RateLimiterPolicyResolver>();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IConfigureOptions<RateLimiterOptions>, ConfigureRateLimiterOptions>());
        services.AddApiPipelineStartupCheck<SocketTransportRateLimitingWarning>();
        services.AddPipelineFeature<ApiPipelineRateLimitingOptions>(
            PipelinePhase.RateLimiting, ApiPipelineConfigurationKeys.RateLimiting, static o => o.Enabled);
        return services;
    }

    private static void RegisterEdgeRateLimiting(
        IServiceCollection services,
        OptionsBuilder<ApiPipelineEdgeRateLimitingOptions> options)
    {
        options.Validated<ApiPipelineEdgeRateLimitingOptions, ApiPipelineEdgeRateLimitingOptionsValidator>();

        // Container-owned, so its timer stops with the host.
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<EdgeRateLimiter>();
        services.AddPipelineFeature<ApiPipelineEdgeRateLimitingOptions>(
            PipelinePhase.EdgeRateLimiting, ApiPipelineConfigurationKeys.EdgeRateLimiting, static o => o.Enabled);
    }


    /// <summary>
    /// Registers response compression, bound from the <c>ApiPipeline:ResponseCompression</c> section.
    /// </summary>
    /// <param name="services">The service collection to add services to.</param>
    /// <param name="configuration">The application configuration, or the pipeline section.</param>
    /// <returns>The same <see cref="IServiceCollection"/> instance to enable fluent chaining.</returns>
    public static IServiceCollection AddApiPipelineResponseCompression(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        return services.AddFeature<ApiPipelineResponseCompressionOptions>(
            services.FeatureSection(configuration, ApiPipelineConfigurationKeys.ResponseCompression),
            static (b, section) => b.Bind(section)
                .BindEmptyList(section, nameof(ApiPipelineResponseCompressionOptions.ExcludedPaths), static o => o.ExcludedPaths = []),
            configure: null,
            RegisterResponseCompression);
    }

    /// <summary>Registers response compression, configured in code.</summary>
    /// <param name="services">The service collection to add services to.</param>
    /// <param name="configure">Configures the options.</param>
    /// <returns>The same <see cref="IServiceCollection"/> instance to enable fluent chaining.</returns>
    public static IServiceCollection AddApiPipelineResponseCompression(
        this IServiceCollection services,
        Action<ApiPipelineResponseCompressionOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        return services.AddFeature(section: null, bind: null, configure, RegisterResponseCompression);
    }

    private static void RegisterResponseCompression(
        IServiceCollection services,
        OptionsBuilder<ApiPipelineResponseCompressionOptions> options)
    {
        // An empty provider list means Brotli plus Gzip to the framework.
        options.Validated<ApiPipelineResponseCompressionOptions, ApiPipelineResponseCompressionOptionsValidator>()
            .Validate(
                static o => !o.Enabled || o.EnableBrotli || o.EnableGzip,
                "ApiPipeline:ResponseCompression: EnableBrotli and EnableGzip are both false while compression " +
                "is enabled. ASP.NET Core treats an empty provider list as Brotli plus Gzip, so this would " +
                "compress with both. Enable at least one, or set Enabled to false.");

        services.AddResponseCompression();

        services.AddFrameworkDefaults<BrotliCompressionProviderOptions, ConfigureBrotliCompressionLevel>();
        services.AddFrameworkDefaults<GzipCompressionProviderOptions, ConfigureGzipCompressionLevel>();
        services.AddFrameworkDefaults<ResponseCompressionOptions, ConfigureResponseCompressionOptions>();
        services.AddPipelineFeature<ApiPipelineResponseCompressionOptions>(
            PipelinePhase.ResponseCompression, ApiPipelineConfigurationKeys.ResponseCompression, static o => o.Enabled);
    }


    /// <summary>
    /// Registers response caching, bound from the <c>ApiPipeline:ResponseCaching</c> section.
    /// </summary>
    /// <param name="services">The service collection to add services to.</param>
    /// <param name="configuration">The application configuration, or the pipeline section.</param>
    /// <returns>The same <see cref="IServiceCollection"/> instance to enable fluent chaining.</returns>
    [Obsolete(ApiPipelineDiagnostics.ResponseCachingObsoleteMessage, DiagnosticId = ApiPipelineDiagnostics.ResponseCachingObsoleteId, UrlFormat = ApiPipelineDiagnostics.UrlFormat)]
    public static IServiceCollection AddApiPipelineResponseCaching(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        return services.AddFeature<ApiPipelineResponseCachingOptions>(
            services.FeatureSection(configuration, ApiPipelineConfigurationKeys.ResponseCaching),
            static (b, section) => b.Bind(section),
            configure: null,
            RegisterResponseCaching);
    }

    /// <summary>Registers response caching, configured in code.</summary>
    /// <param name="services">The service collection to add services to.</param>
    /// <param name="configure">Configures the options.</param>
    /// <returns>The same <see cref="IServiceCollection"/> instance to enable fluent chaining.</returns>
    [Obsolete(ApiPipelineDiagnostics.ResponseCachingObsoleteMessage, DiagnosticId = ApiPipelineDiagnostics.ResponseCachingObsoleteId, UrlFormat = ApiPipelineDiagnostics.UrlFormat)]
    public static IServiceCollection AddApiPipelineResponseCaching(
        this IServiceCollection services,
        Action<ApiPipelineResponseCachingOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        return services.AddFeature(section: null, bind: null, configure, RegisterResponseCaching);
    }

    private static void RegisterResponseCaching(
        IServiceCollection services,
        OptionsBuilder<ApiPipelineResponseCachingOptions> options)
    {
        options.Validated<ApiPipelineResponseCachingOptions, ApiPipelineResponseCachingOptionsValidator>()
            .Validate<IOptions<ApiPipelineOutputCachingOptions>>(
                (settings, outputCaching) => !settings.Enabled || !outputCaching.Value.Enabled,
                "ApiPipeline:ResponseCaching:Enabled and ApiPipeline:OutputCaching:Enabled cannot both be true. " +
                "Output caching is the modern replacement and supports distributed stores and " +
                "tag-based eviction — prefer it, and disable response caching.");

        services.AddResponseCaching();
        services.AddFrameworkDefaults<ResponseCachingOptions, ConfigureResponseCachingOptions>();
        services.AddPipelineFeature<ApiPipelineResponseCachingOptions>(
            PipelinePhase.ResponseCaching, ApiPipelineConfigurationKeys.ResponseCaching, static o => o.Enabled);
    }


    /// <summary>
    /// Registers ASP.NET Core output caching, bound from the <c>ApiPipeline:OutputCaching</c> section.
    /// </summary>
    /// <param name="services">The service collection to add services to.</param>
    /// <param name="configuration">The application configuration, or the pipeline section.</param>
    /// <returns>The same <see cref="IServiceCollection"/> instance to enable fluent chaining.</returns>
    public static IServiceCollection AddApiPipelineOutputCaching(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        return services.AddFeature<ApiPipelineOutputCachingOptions>(
            services.FeatureSection(configuration, ApiPipelineConfigurationKeys.OutputCaching),
            static (b, section) => b.Bind(section).ValidateDurations(
                    section,
                    new DurationKey("DefaultExpiration", FormerName: null)),
            configure: null,
            RegisterOutputCaching);
    }

    /// <summary>Registers ASP.NET Core output caching, configured in code.</summary>
    /// <param name="services">The service collection to add services to.</param>
    /// <param name="configure">Configures the options.</param>
    /// <returns>The same <see cref="IServiceCollection"/> instance to enable fluent chaining.</returns>
    public static IServiceCollection AddApiPipelineOutputCaching(
        this IServiceCollection services,
        Action<ApiPipelineOutputCachingOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        return services.AddFeature(section: null, bind: null, configure, RegisterOutputCaching);
    }

    private static void RegisterOutputCaching(
        IServiceCollection services,
        OptionsBuilder<ApiPipelineOutputCachingOptions> options)
    {
        options.Validated<ApiPipelineOutputCachingOptions, ApiPipelineOutputCachingOptionsValidator>();
        services.AddOutputCache();
        services.AddFrameworkDefaults<OutputCacheOptions, ConfigureOutputCacheOptions>();
        services.AddPipelineFeature<ApiPipelineOutputCachingOptions>(
            PipelinePhase.OutputCaching, ApiPipelineConfigurationKeys.OutputCaching, static o => o.Enabled);
    }


    /// <summary>
    /// Registers the security headers, bound from the <c>ApiPipeline:SecurityHeaders</c> section.
    /// </summary>
    /// <param name="services">The service collection to add services to.</param>
    /// <param name="configuration">The application configuration, or the pipeline section.</param>
    /// <returns>The same <see cref="IServiceCollection"/> instance to enable fluent chaining.</returns>
    public static IServiceCollection AddApiPipelineSecurityHeaders(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        return services.AddFeature<ApiPipelineSecurityHeadersOptions>(
            services.FeatureSection(configuration, ApiPipelineConfigurationKeys.SecurityHeaders),
            static (b, section) => b.Bind(section).ValidateDurations(
                    section,
                    new DurationKey("StrictTransportSecurityMaxAge", "StrictTransportSecurityMaxAgeSeconds")),
            configure: null,
            RegisterSecurityHeaders);
    }

    /// <summary>Registers the security headers, configured in code.</summary>
    /// <param name="services">The service collection to add services to.</param>
    /// <param name="configure">Configures the options.</param>
    /// <returns>The same <see cref="IServiceCollection"/> instance to enable fluent chaining.</returns>
    public static IServiceCollection AddApiPipelineSecurityHeaders(
        this IServiceCollection services,
        Action<ApiPipelineSecurityHeadersOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        return services.AddFeature(section: null, bind: null, configure, RegisterSecurityHeaders);
    }

    private static void RegisterSecurityHeaders(
        IServiceCollection services,
        OptionsBuilder<ApiPipelineSecurityHeadersOptions> options)
    {
        options.Validated<ApiPipelineSecurityHeadersOptions, ApiPipelineSecurityHeadersOptionsValidator>();

        services.AddFrameworkDefaults<HstsOptions, ConfigureHstsOptions>();
        services.AddPipelineFeature<ApiPipelineSecurityHeadersOptions>(
            PipelinePhase.SecurityHeaders, ApiPipelineConfigurationKeys.SecurityHeaders, static o => o.Enabled);
    }


    /// <summary>
    /// Registers the CORS policy, bound from the <c>ApiPipeline:Cors</c> section.
    /// </summary>
    /// <param name="services">The service collection to add services to.</param>
    /// <param name="configuration">The application configuration, or the pipeline section.</param>
    /// <returns>The same <see cref="IServiceCollection"/> instance to enable fluent chaining.</returns>
    public static IServiceCollection AddApiPipelineCors(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        return services.AddFeature<ApiPipelineCorsOptions>(
            services.FeatureSection(configuration, ApiPipelineConfigurationKeys.Cors),
            static (b, section) => b.Bind(section)
                .BindEmptyList(section, nameof(ApiPipelineCorsOptions.AllowedMethods), static o => o.AllowedMethods = [])
                .BindEmptyList(section, nameof(ApiPipelineCorsOptions.AllowedHeaders), static o => o.AllowedHeaders = [])
                .ValidateDurations(section, new DurationKey("PreflightMaxAge", FormerName: null)),
            configure: null,
            RegisterCors);
    }

    /// <summary>Registers the CORS policy, configured in code.</summary>
    /// <param name="services">The service collection to add services to.</param>
    /// <param name="configure">Configures the options.</param>
    /// <returns>The same <see cref="IServiceCollection"/> instance to enable fluent chaining.</returns>
    public static IServiceCollection AddApiPipelineCors(
        this IServiceCollection services,
        Action<ApiPipelineCorsOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        return services.AddFeature(section: null, bind: null, configure, RegisterCors);
    }

    private static void RegisterCors(IServiceCollection services, OptionsBuilder<ApiPipelineCorsOptions> options)
    {
        options
            .Validate(
                c => !c.AllowCredentials || (c.AllowedOrigins is { Count: > 0 }),
                "When AllowCredentials is true, AllowedOrigins must be configured (CORS does not allow wildcard origin with credentials).")
            .Validate(
                c => !c.AllowCredentials
                     || c.AllowedOrigins is null
                     || !c.AllowedOrigins.Contains("*", StringComparer.Ordinal),
                "When AllowCredentials is true, AllowedOrigins must not contain the wildcard \"*\". " +
                "The CORS protocol does not allow a wildcard origin together with credentials — " +
                "list the individual origins instead.")
            .Validate(
                c => !c.Enabled || ConfiguredCorsPolicy.TryBuildConfiguredPolicy(c, metrics: null, out _, out _),
                "ApiPipeline:Cors could not be turned into a valid CORS policy. Review AllowedOrigins, " +
                "AllowedMethods, AllowedHeaders, and AllowCredentials.")
            .Validate(
                c => !c.Enabled || c.AllowAllInDevelopment || c.AllowedMethods is null || HasConfiguredCorsValues(c.AllowedMethods),
                "When CORS is enabled, AllowedMethods must either be absent — which applies the safe default set " +
                "(GET, POST, PUT, PATCH, DELETE, OPTIONS) — or include at least one non-empty value. " +
                "Use ['*'] explicitly if unrestricted methods are intended.")
            .Validate(
                c => c.PreflightMaxAge is not { } maxAge
                     || (maxAge >= TimeSpan.Zero
                         && maxAge <= ApiPipelineCorsOptions.MaximumPreflightMaxAge
                         && maxAge.Ticks % TimeSpan.TicksPerSecond == 0),
                "ApiPipeline:Cors:PreflightMaxAge must be whole seconds between \"00:00:00\" and \"1.00:00:00\": " +
                "Access-Control-Max-Age counts whole seconds, and browsers cap it at 2 hours (Chromium) or 24 hours " +
                "(Firefox).")
            .Validate(
                c => !c.Enabled || c.AllowAllInDevelopment || c.AllowedHeaders is null || HasConfiguredCorsValues(c.AllowedHeaders),
                "When CORS is enabled, AllowedHeaders must either be absent — which applies the safe default set " +
                "(Content-Type, Authorization, X-Correlation-Id) — or include at least one non-empty value. " +
                "Use ['*'] explicitly if unrestricted headers are intended.")
            .ValidateOnStart();

        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<ApiPipelineCorsOptions>, CorsAllowedOriginsValidator>());

        services.AddCors();
        services.TryAddSingleton<ConfiguredCorsPolicy>();

        services.AddApiPipelineStartupCheck<CorsOverrideStartupGuard>();
        services.AddPipelineFeature<ApiPipelineCorsOptions>(
            PipelinePhase.Cors, ApiPipelineConfigurationKeys.Cors, static o => o.Enabled);

        // Wrap the provider in effect (the application's, if any) at its own lifetime.
        var inner = services.LastOrDefault(d => d.ServiceType == typeof(ICorsPolicyProvider) && !d.IsKeyedService)!;
        services.RemoveAll<ICorsPolicyProvider>();
        var createInner = ConfiguredCorsPolicyProvider.InnerFactory(inner);
        services.Add(new ServiceDescriptor(
            typeof(ICorsPolicyProvider),
            sp =>
            {
                var (provider, owned) = createInner(sp);
                return new ConfiguredCorsPolicyProvider(sp.GetRequiredService<ConfiguredCorsPolicy>(), provider, owned);
            },
            ConfiguredCorsPolicyProvider.WrapperLifetime(inner)));
    }


    /// <summary>
    /// Registers Kestrel and form request limits, bound from the <c>ApiPipeline:RequestLimits</c> section.
    /// </summary>
    /// <param name="services">The service collection to add services to.</param>
    /// <param name="configuration">The application configuration, or the pipeline section.</param>
    /// <returns>The same <see cref="IServiceCollection"/> instance to enable fluent chaining.</returns>
    public static IServiceCollection AddApiPipelineRequestLimits(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        return services.AddFeature<ApiPipelineRequestLimitsOptions>(
            services.FeatureSection(configuration, ApiPipelineConfigurationKeys.RequestLimits),
            static (b, section) => b.Bind(section).ValidateDurations(
                    section,
                    new DurationKey("RequestHeadersTimeout", "RequestHeadersTimeoutSeconds"),
                    new DurationKey("KeepAliveTimeout", "KeepAliveTimeoutSeconds"),
                    new DurationKey("MinRequestBodyDataRateGracePeriod", "MinRequestBodyDataRateGracePeriodSeconds")),
            configure: null,
            RegisterRequestLimits);
    }

    /// <summary>Registers Kestrel and form request limits, configured in code.</summary>
    /// <param name="services">The service collection to add services to.</param>
    /// <param name="configure">Configures the options.</param>
    /// <returns>The same <see cref="IServiceCollection"/> instance to enable fluent chaining.</returns>
    public static IServiceCollection AddApiPipelineRequestLimits(
        this IServiceCollection services,
        Action<ApiPipelineRequestLimitsOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        return services.AddFeature(section: null, bind: null, configure, RegisterRequestLimits);
    }

    private static void RegisterRequestLimits(
        IServiceCollection services,
        OptionsBuilder<ApiPipelineRequestLimitsOptions> options)
    {
        options.Validated<ApiPipelineRequestLimitsOptions, ApiPipelineRequestLimitsOptionsValidator>();

        services.AddFrameworkDefaults<KestrelServerOptions, ConfigureKestrelOptions>();

        services.AddServerSpecificDefaults(
            RequestLimitsServerSupport.IisOptionsAvailable, RequestLimitsServerSupport.HttpSysOptionsAvailable);
        services.AddApiPipelineStartupCheck<RequestLimitsServerWarning>();

        services.AddFrameworkDefaults<FormOptions, ConfigureFormOptions>();
    }

    /// <summary>
    /// The IIS in-process and HTTP.sys defaults, each only where the runtime has its options type.
    /// Source-built runtimes (Homebrew, distribution packages) ship those assemblies as empty stubs,
    /// and naming a missing type here would throw <see cref="TypeLoadException"/> from every
    /// <c>AddApiPipeline</c> call, request limits enabled or not.
    /// </summary>
    internal static void AddServerSpecificDefaults(this IServiceCollection services, bool iis, bool httpSys)
    {
        if (iis)
        {
            AddIisDefaults(services);
        }

        if (httpSys)
        {
            AddHttpSysDefaults(services);
        }
    }

    // Not inlined, so the caller is compiled without resolving the server's types.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void AddIisDefaults(IServiceCollection services) =>
        services.AddFrameworkDefaults<IISServerOptions, ConfigureIisServerOptions>();

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void AddHttpSysDefaults(IServiceCollection services) =>
        services.AddFrameworkDefaults<HttpSysOptions, ConfigureHttpSysOptions>();


    /// <summary>
    /// Registers forwarded headers processing, bound from the <c>ApiPipeline:ForwardedHeaders</c> section.
    /// </summary>
    /// <param name="services">The service collection to add services to.</param>
    /// <param name="configuration">The application configuration, or the pipeline section.</param>
    /// <returns>The same <see cref="IServiceCollection"/> instance to enable fluent chaining.</returns>
    public static IServiceCollection AddApiPipelineForwardedHeaders(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        return services.AddFeature<ApiPipelineForwardedHeadersOptions>(
            services.FeatureSection(configuration, ApiPipelineConfigurationKeys.ForwardedHeaders),
            static (b, section) => b.Bind(section),
            configure: null,
            RegisterForwardedHeaders);
    }

    /// <summary>Registers forwarded headers processing, configured in code.</summary>
    /// <param name="services">The service collection to add services to.</param>
    /// <param name="configure">Configures the options.</param>
    /// <returns>The same <see cref="IServiceCollection"/> instance to enable fluent chaining.</returns>
    public static IServiceCollection AddApiPipelineForwardedHeaders(
        this IServiceCollection services,
        Action<ApiPipelineForwardedHeadersOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        return services.AddFeature(section: null, bind: null, configure, RegisterForwardedHeaders);
    }

    private static void RegisterForwardedHeaders(
        IServiceCollection services,
        OptionsBuilder<ApiPipelineForwardedHeadersOptions> options)
    {
        options.Validated<ApiPipelineForwardedHeadersOptions, ApiPipelineForwardedHeadersOptionsValidator>();
        services.AddPipelineFeature<ApiPipelineForwardedHeadersOptions>(
            PipelinePhase.ForwardedHeaders, ApiPipelineConfigurationKeys.ForwardedHeaders, static o => o.Enabled);
    }


    /// <summary>
    /// Registers ASP.NET Core request timeouts, bound from the <c>ApiPipeline:RequestTimeouts</c> section.
    /// </summary>
    /// <param name="services">The service collection to add services to.</param>
    /// <param name="configuration">The application configuration, or the pipeline section.</param>
    /// <returns>The same <see cref="IServiceCollection"/> instance to enable fluent chaining.</returns>
    public static IServiceCollection AddApiPipelineRequestTimeouts(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        return services.AddFeature<ApiPipelineRequestTimeoutsOptions>(
            services.FeatureSection(configuration, ApiPipelineConfigurationKeys.RequestTimeouts),
            static (b, section) => b.Bind(section).ValidateDurations(
                    section,
                    new DurationKey("DefaultTimeout", "DefaultTimeoutSeconds")),
            configure: null,
            RegisterRequestTimeouts);
    }

    /// <summary>Registers ASP.NET Core request timeouts, configured in code.</summary>
    /// <param name="services">The service collection to add services to.</param>
    /// <param name="configure">Configures the options.</param>
    /// <returns>The same <see cref="IServiceCollection"/> instance to enable fluent chaining.</returns>
    public static IServiceCollection AddApiPipelineRequestTimeouts(
        this IServiceCollection services,
        Action<ApiPipelineRequestTimeoutsOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        return services.AddFeature(section: null, bind: null, configure, RegisterRequestTimeouts);
    }

    private static void RegisterRequestTimeouts(
        IServiceCollection services,
        OptionsBuilder<ApiPipelineRequestTimeoutsOptions> options)
    {
        options.Validated<ApiPipelineRequestTimeoutsOptions, ApiPipelineRequestTimeoutsOptionsValidator>();
        services.AddRequestTimeouts();
        services.AddFrameworkDefaults<RequestTimeoutOptions, ConfigureRequestTimeoutOptions>();
        services.AddPipelineFeature<ApiPipelineRequestTimeoutsOptions>(
            PipelinePhase.RequestTimeouts, ApiPipelineConfigurationKeys.RequestTimeouts, static o => o.Enabled);
    }


    /// <summary>
    /// Registers support for request body size telemetry (<c>WithRequestSizeTracking()</c>).
    /// </summary>
    /// <param name="services">The service collection to add services to.</param>
    /// <returns>The same <see cref="IServiceCollection"/> instance to enable fluent chaining.</returns>
    public static IServiceCollection AddApiPipelineRequestSizeTracking(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services.AddApiPipelineCore();
    }


    /// <summary>
    /// Binds the section once, applies <paramref name="configure"/> on every call, and registers the
    /// feature once. <paramref name="bind"/> is a lambda at each call site so the binding source
    /// generator sees the concrete options type (trimming, AOT).
    /// </summary>
    private static IServiceCollection AddFeature<TOptions>(
        this IServiceCollection services,
        IConfigurationSection? section,
        Action<OptionsBuilder<TOptions>, IConfigurationSection>? bind,
        Action<TOptions>? configure,
        Action<IServiceCollection, OptionsBuilder<TOptions>> register)
        where TOptions : class
    {
        services.AddApiPipelineCore();

        var options = services.AddOptions<TOptions>();

        if (section is not null && bind is not null && services.ClaimBinding<TOptions>(section))
        {
            bind(options, section);
        }

        if (configure is not null)
        {
            options.Configure(configure);
        }

        if (services.TryClaim<FeatureClaim<TOptions>>())
        {
            register(services, options);
        }

        return services;
    }

    /// <summary>Services every feature needs.</summary>
    private static IServiceCollection AddApiPipelineCore(this IServiceCollection services)
    {
        if (!services.TryClaim<CoreClaim>())
        {
            return services;
        }

        services.AddMetrics();
        services.TryAddSingleton<ApiPipelineMetrics>();

        // UseApiPipeline calls UseRouting(); a plain host may not register it.
        services.AddRouting();

        services.TryAddSingleton<PipelineWiring>();
        services.AddApiPipelineStartupCheck<UnwiredPipelineFeaturesWarning>();
        return services;
    }

    private static OptionsBuilder<TOptions> Validated<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] TOptions,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TValidator>(
        this OptionsBuilder<TOptions> options)
        where TOptions : class
        where TValidator : class, IValidateOptions<TOptions>
    {
        options.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<TOptions>, TValidator>());
        return options.ValidateOnStart();
    }

    /// <summary>
    /// <paramref name="key"/> under the pipeline section: <paramref name="configuration"/> when it is
    /// a section, its <c>ApiPipeline</c> child otherwise.
    /// </summary>
    private static IConfigurationSection FeatureSection(
        this IServiceCollection services,
        IConfiguration configuration,
        string key)
    {
        if (configuration is IConfigurationSection section)
        {
            PipelineSection.ThrowIfFeatureSection(section, key, ApiPipelineConfigurationKeys.Section, key);
            return section.GetSection(key);
        }

        if (services.TryClaim<LegacySectionsClaim>())
        {
            services.AddApiPipelineStartupCheck(sp =>
                new LegacyConfigurationSectionsWarning(
                    configuration,
                    sp.GetRequiredService<ILogger<LegacyConfigurationSectionsWarning>>()));
        }

        return configuration.GetSection(ApiPipelineConfigurationKeys.Section).GetSection(key);
    }

    private static bool HasConfiguredCorsValues(IList<string>? values) =>
        values is { Count: > 0 } && values.All(v => !string.IsNullOrWhiteSpace(v));

    private sealed class FeatureClaim<T>
        where T : class;

    private sealed class CoreClaim;

    private sealed class LegacySectionsClaim;

    private sealed class RateLimiterServicesClaim;
}
