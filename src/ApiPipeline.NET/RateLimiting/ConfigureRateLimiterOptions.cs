using System.Threading.RateLimiting;
using ApiPipeline.NET.Observability;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ApiPipeline.NET.RateLimiting;

/// <summary>
/// Configures the framework's <see cref="RateLimiterOptions"/> from the validated options: global
/// limiter, one named policy each, and the rejection response. Policies added in code count too.
/// </summary>
internal sealed class ConfigureRateLimiterOptions : IConfigureOptions<RateLimiterOptions>
{
    private readonly IOptions<ApiPipelineRateLimitingOptions> _startupOptions;
    private readonly RateLimiterPolicyResolver _resolver;
    private readonly ApiPipelineMetrics _metrics;
    private readonly IApiPipelineRateLimiterFactory _factory;
    private readonly IRateLimitPartitionKeyResolver[] _keyResolvers;
    private readonly ILogger _logger;

    public ConfigureRateLimiterOptions(
        IOptions<ApiPipelineRateLimitingOptions> startupOptions,
        RateLimiterPolicyResolver resolver,
        ApiPipelineMetrics metrics,
        IApiPipelineRateLimiterFactory factory,
        IEnumerable<IRateLimitPartitionKeyResolver> keyResolvers,
        ILoggerFactory loggerFactory)
    {
        _startupOptions = startupOptions;
        _resolver = resolver;
        _metrics = metrics;
        _factory = factory;
        _keyResolvers = [.. keyResolvers];
        _logger = loggerFactory.CreateLogger("ApiPipeline.NET.RateLimiting");
    }

    public void Configure(RateLimiterOptions options)
    {
        // Off: leave the application's own AddRateLimiter settings alone.
        if (!_startupOptions.Value.Enabled)
        {
            return;
        }

        var resolver = _resolver;
        var metrics = _metrics;
        var factory = _factory;
        var budget = new RateLimitPartitionBudget(
            _startupOptions.Value.MaxIpPartitions, RateLimitPartitionBudget.MainLimiter, metrics, _logger);
        var identityBudget = new RateLimitPartitionBudget(
            _startupOptions.Value.MaxIdentityPartitions, RateLimitPartitionBudget.MainLimiterIdentities, metrics, _logger);
        var partitioning = new Partitioning(resolver, factory, _keyResolvers, _logger, budget, identityBudget);

        // The framework default is 503, which an application's own OnRejected would expose.
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

        options.OnRejected = (context, _) =>
        {
            var httpContext = context.HttpContext;
            var policyName = httpContext.Items[RateLimitContextItems.PolicyName] as string;
            var partitionType = httpContext.Items[RateLimitContextItems.PartitionType] as string ?? "anonymous";
            metrics.RateLimitRejected(policyName, partitionType);

            var rejectedPolicy = httpContext.Items[RateLimitContextItems.PolicyConfig] as ApiPipelineRateLimitPolicy;

            return new ValueTask(RateLimitRejection.WriteAsync(
                httpContext,
                policyName,
                retryAfterSeconds: context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter)
                    ? (int)Math.Ceiling(retryAfter.TotalSeconds)
                    : rejectedPolicy?.FallbackRetryAfterSeconds,
                permitLimit: rejectedPolicy?.AdvertisedQuota,
                windowSeconds: rejectedPolicy?.WindowInSeconds,
                emitQuotaHeaders: resolver.Current.EmitRateLimitHeadersOnRejection));
        };

        options.GlobalLimiter = CreateGlobalLimiter(_startupOptions.Value, partitioning);

        foreach (var configured in _startupOptions.Value.Policies)
        {
            if (string.IsNullOrWhiteSpace(configured.Name))
            {
                continue;
            }

            var policyName = configured.Name;
            options.AddPolicy(policyName, httpContext => Partition(httpContext, policyName, partitioning));
        }
    }

    /// <summary>
    /// The framework's <c>GlobalLimiter</c>: <c>DefaultPolicy</c> as a fallback that steps aside for
    /// an endpoint's own policy, chained after <c>GlobalPolicy</c> when set. When the two are the same
    /// policy, one limiter on every request, never the bucket spent twice.
    /// </summary>
    private static PartitionedRateLimiter<HttpContext> CreateGlobalLimiter(
        ApiPipelineRateLimitingOptions settings,
        Partitioning partitioning)
    {
        var defaultPolicy = settings.DefaultPolicy;
        var globalPolicy = string.IsNullOrWhiteSpace(settings.GlobalPolicy) ? null : settings.GlobalPolicy;

        PartitionedRateLimiter<HttpContext> Everywhere(string policy) =>
            PartitionedRateLimiter.Create<HttpContext, string>(httpContext => Partition(httpContext, policy, partitioning));

        if (globalPolicy is not null && string.Equals(globalPolicy, defaultPolicy, StringComparison.OrdinalIgnoreCase))
        {
            return Everywhere(defaultPolicy);
        }

        var fallback = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
            httpContext.GetEndpoint()?.Metadata.GetMetadata<EnableRateLimitingAttribute>() is not null
                ? RateLimitPartition.GetNoLimiter("endpoint-policy")
                : Partition(httpContext, defaultPolicy, partitioning));

        return globalPolicy is null
            ? fallback
            : PartitionedRateLimiter.CreateChained(Everywhere(globalPolicy), fallback);
    }

    private sealed record Partitioning(
        RateLimiterPolicyResolver Resolver,
        IApiPipelineRateLimiterFactory Factory,
        IRateLimitPartitionKeyResolver[] KeyResolvers,
        ILogger Logger,
        RateLimitPartitionBudget Budget,
        RateLimitPartitionBudget IdentityBudget);

    private static RateLimitPartition<string> Partition(
        HttpContext context,
        string policyName,
        Partitioning partitioning)
    {
        context.Items[RateLimitContextItems.PolicyName] = policyName;

        if (!partitioning.Resolver.TryResolve(policyName, out var policy, out var scope))
        {
            return RateLimitPartition.GetNoLimiter("no-policy");
        }

        context.Items[RateLimitContextItems.PolicyConfig] = policy;
        return PartitionFor(context, policy, partitioning, scope);
    }

    /// <summary>Asks an application resolver; one that throws is logged and skipped, not a 500.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Application code on the request path; any exception it throws falls through to the built-in partitioning.")]
    private static RateLimitPartitionKey? TryResolve(
        IRateLimitPartitionKeyResolver keyResolver,
        HttpContext context,
        ApiPipelineRateLimitPolicy policy,
        ILogger logger)
    {
        try
        {
            return keyResolver.Resolve(context, policy);
        }
        catch (Exception ex)
        {
            logger.PartitionKeyResolverFailed(ex, keyResolver.GetType().FullName ?? keyResolver.GetType().Name, policy.Name);
            return null;
        }
    }

    /// <summary>
    /// First match: resolver, claim, user id, allow-listed API key (bounded by
    /// <c>MaxIdentityPartitions</c>, else the IP), client IP (bounded by <c>MaxIpPartitions</c>), then
    /// <c>AnonymousFallback</c>.
    /// </summary>
    private static RateLimitPartition<string> PartitionFor(
        HttpContext context,
        ApiPipelineRateLimitPolicy policy,
        Partitioning partitioning,
        string scope)
    {
        var factory = partitioning.Factory;
        var options = partitioning.Resolver.Current;

        if (IdentityKey(context, policy, partitioning, scope) is { } identity)
        {
            var identityBudget = partitioning.IdentityBudget;
            if (identityBudget.TryAdmit(identity.Key))
            {
                context.Items[RateLimitContextItems.PartitionType] = identity.Type;
                return RateLimitPartition.Get(identity.Key, key => identityBudget.Track(key, factory.Create(key, policy)));
            }
        }

        var remoteIp = context.Connection.RemoteIpAddress;
        if (remoteIp is not null)
        {
            context.Items[RateLimitContextItems.PartitionType] = "ip";

            // Callers can mint addresses, so these are bounded.
            var budget = partitioning.Budget;
            var ipKey = budget.Admit(
                RateLimitPartitionKeys.ForIp(remoteIp, options.IPv6PartitionPrefixLength, scope),
                $"ip:overflow|{scope}");
            return RateLimitPartition.Get(ipKey, key => budget.Track(key, factory.Create(key, policy)));
        }

        context.Items[RateLimitContextItems.PartitionType] = "anonymous";
        return options.AnonymousFallback switch
        {
            // Not PermitLimit = 0: that throws, a 500.
            AnonymousFallbackBehavior.Reject =>
                RateLimitPartition.Get("ip:null:reject", _ => DenyAllRateLimiter.Instance),

            AnonymousFallbackBehavior.Allow =>
                RateLimitPartition.GetNoLimiter("ip:null:allow"),

            _ => CreatePartition(factory, $"ip:anonymous|{scope}", policy),
        };
    }

    private static (string Key, string Type)? IdentityKey(
        HttpContext context,
        ApiPipelineRateLimitPolicy policy,
        Partitioning partitioning,
        string scope)
    {
        var resolver = partitioning.Resolver;
        var options = resolver.Current;

        foreach (var keyResolver in partitioning.KeyResolvers)
        {
            if (TryResolve(keyResolver, context, policy, partitioning.Logger) is { } resolved)
            {
                return (RateLimitPartitionKeys.ForResolved(resolved, scope), resolved.Type);
            }
        }

        var user = context.User;
        var authenticated = user.Identity?.IsAuthenticated == true;
        if (authenticated)
        {
            foreach (var claimType in resolver.PartitionClaims)
            {
                if (user.FindFirst(claimType)?.Value is { } claimValue && !string.IsNullOrWhiteSpace(claimValue))
                {
                    return (RateLimitPartitionKeys.ForClaim(claimType, claimValue, scope), "claim");
                }
            }

            // The JWT handler maps "sub" to NameIdentifier.
            var userId = user.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                ?? user.FindFirst("sub")?.Value
                ?? user.FindFirst("nameid")?.Value;

            if (!string.IsNullOrWhiteSpace(userId))
            {
                return (RateLimitPartitionKeys.ForUser(userId, scope), "user");
            }
        }

        if (options.EnableApiKeyPartitioning)
        {
            var apiKey = context.Request.Headers[options.ApiKeyHeader].FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                // Only allow-listed keys, or callers could mint buckets.
                var apiKeyHash = RateLimitPartitionKeys.HashApiKey(apiKey);
                if (resolver.IsKnownApiKeyHash(apiKeyHash))
                {
                    return (RateLimitPartitionKeys.ForApiKeyHash(apiKeyHash, scope), "apikey");
                }
            }
        }

        return null;
    }

    private static RateLimitPartition<string> CreatePartition(
        IApiPipelineRateLimiterFactory factory,
        string partitionKey,
        ApiPipelineRateLimitPolicy policy) =>
        RateLimitPartition.Get(partitionKey, key => factory.Create(key, policy));
}
