using ApiPipeline.NET.Cors;
using ApiPipeline.NET.Pipeline;
using ApiPipeline.NET.RateLimiting;

namespace ApiPipeline.NET.Observability;

/// <summary>
/// The core package's log messages. Event ids 1-99 (Redis 100-199, Versioning 200-299) are stable;
/// see docs/OBSERVABILITY.md.
/// </summary>
internal static partial class Log
{
    // ------------------------------------------------------------------ correlation (request path)

    [LoggerMessage(1, LogLevel.Debug,
        "Rejected invalid correlation ID from request header, generated {CorrelationId}")]
    public static partial void CorrelationIdRejected(this ILogger logger, string correlationId);

    // ------------------------------------------------------------------ pipeline composition

    [LoggerMessage(10, LogLevel.Information,
        "ApiPipeline.NET pipeline composed, in execution order: [{Phases}]. A requested " +
        "phase whose own Enabled flag is false adds no middleware and is still listed " +
        "here; a phase that was never requested is not.")]
    public static partial void PipelineComposed(this ILogger logger, string phases);

    [LoggerMessage(11, LogLevel.Warning,
        "ApiPipeline.NET pipeline order: {Phase} was requested more than once; it is applied " +
        "once, at its first position. Remove the extra With{Phase}() call.")]
    public static partial void PhaseRequestedTwice(this ILogger logger, PipelinePhase phase);

    [LoggerMessage(12, LogLevel.Warning,
        "ApiPipeline.NET pipeline order: {Then} runs before {First}, but {First} should run " +
        "first: {Reason}. Recommended: call With{First}() before With{Then}(). The pipeline " +
        "runs in the order written; ignore this warning only if the order is deliberate.")]
    public static partial void PhaseOrderViolated(
        this ILogger logger, PipelinePhase then, PipelinePhase first, string reason);

    [LoggerMessage(13, LogLevel.Warning,
        "ApiPipeline.NET: {Feature}:Enabled is true, but its phase was never added to the pipeline, " +
        "so the feature does nothing. Add With{Phase}() to UseApiPipeline(...), or set " +
        "{Feature}:Enabled to false. Middleware added outside UseApiPipeline (WithMiddleware, " +
        "app.Use...) is not seen by this check.")]
    public static partial void FeatureNotInPipeline(this ILogger logger, string feature, PipelinePhase phase);

    [LoggerMessage(14, LogLevel.Warning,
        "ApiPipeline.NET: UseApiPipeline(...) was never called, so these enabled features add no " +
        "middleware and do nothing: {Features}. Call app.UseApiPipeline(...) with their With...() " +
        "phases, or set their Enabled flags to false.")]
    public static partial void UseApiPipelineNeverCalled(this ILogger logger, string features);

    [LoggerMessage(15, LogLevel.Warning,
        "ApiPipeline.NET: With{Phase}() is in the pipeline, but {Feature}:Enabled is false, so the " +
        "phase adds no middleware and the feature does nothing. Set {Feature}:Enabled to true to " +
        "turn it on, or remove With{Phase}(). Expected only where the feature is switched off on " +
        "purpose for this environment.")]
    public static partial void PhaseFeatureDisabled(this ILogger logger, PipelinePhase phase, string feature);

    // ------------------------------------------------------------------ rate limiting

    [LoggerMessage(20, LogLevel.Information,
        "Edge rate limiting enabled: {PermitLimit} requests per {Window} per client address, " +
        "applied before authentication. This limit is process-local — with N replicas the effective " +
        "ceiling is {PermitLimit} × N — and is shared by every caller behind one address (NAT, mobile " +
        "carrier gateway). Enforce per-client quotas with ApiPipeline:RateLimiting, not this.")]
    public static partial void EdgeRateLimitingEnabled(this ILogger logger, int permitLimit, TimeSpan window);

    [LoggerMessage(21, LogLevel.Information,
        "Rate limiting enabled. Default policy (endpoints without their own): '{DefaultPolicy}'. " +
        "Global policy (every endpoint, on top of its own): '{GlobalPolicy}'. Registered named policies: " +
        "[{Policies}]. Policies are registered at startup; adding one requires a restart.")]
    public static partial void RateLimitingEnabled(this ILogger logger, string? defaultPolicy, string globalPolicy, string policies);

    [LoggerMessage(22, LogLevel.Warning, SocketTransportRateLimitingWarning.Message)]
    public static partial void RateLimitingOnSocketTransport(this ILogger logger, string address);

    [LoggerMessage(23, LogLevel.Error,
        "Rate limiting: the partition key resolver {Resolver} threw for policy '{Policy}'. The request is " +
        "partitioned by the next resolver or the built-in chain (user, API key, client IP) instead; fix " +
        "the resolver.")]
    public static partial void PartitionKeyResolverFailed(this ILogger logger, Exception exception, string resolver, string policy);

    [LoggerMessage(24, LogLevel.Warning,
        "{Limiter}: {MaxIpPartitions} client-address partitions are live, its MaxIpPartitions. Requests from " +
        "addresses without a partition now share one overflow bucket per policy (with that policy's limits) " +
        "until idle partitions expire. Either many distinct addresses are calling (a distributed flood), or " +
        "MaxIpPartitions is too low for this traffic. Logged at most once a minute.")]
    public static partial void RateLimitPartitionsExhausted(this ILogger logger, string limiter, int maxIpPartitions);

    [LoggerMessage(25, LogLevel.Warning,
        "ApiPipeline:RateLimiting: {MaxIdentityPartitions} identity partitions (resolver, claim, user, API key) " +
        "are live, its MaxIdentityPartitions. Callers without a partition are now limited by their client " +
        "address's partition until idle partitions expire. Either very many distinct identities are calling " +
        "(many accounts, or a partition resolver keyed on a value with more variety than expected), or " +
        "MaxIdentityPartitions is too low for this traffic. Logged at most once a minute.")]
    public static partial void IdentityPartitionsExhausted(this ILogger logger, int maxIdentityPartitions);

    // ------------------------------------------------------------------ response compression

    [LoggerMessage(30, LogLevel.Warning,
        "ResponseCompression: EnableForHttps is true. Ensure your API never mixes " +
        "attacker-controlled input with secrets in the same compressed response (BREACH/CRIME risk).")]
    public static partial void CompressionEnabledForHttps(this ILogger logger);

    [LoggerMessage(31, LogLevel.Information,
        "ResponseCompression is enabled but EnableForHttps is false, so no response will be " +
        "compressed on any request the server sees as HTTPS. Behind a TLS-terminating proxy " +
        "with forwarded headers applied that is every request, which makes this " +
        "configuration inert. EnableForHttps defaults to false because compressing a " +
        "response that mixes attacker-controlled input with secrets is a BREACH/CRIME " +
        "risk — set ApiPipeline:ResponseCompression:EnableForHttps to true once you have " +
        "confirmed your responses do not, or disable compression entirely.")]
    public static partial void CompressionNotForHttps(this ILogger logger);

    // ------------------------------------------------------------------ CORS

    [LoggerMessage(40, LogLevel.Warning,
        "CORS: AllowAll policy is active (AllowAllInDevelopment=true). " +
        "All origins, methods, and headers are allowed. Do not use in production.")]
    public static partial void CorsAllowAllActive(this ILogger logger);

    // 41: reserved; do not reuse.

    [LoggerMessage(42, LogLevel.Warning, CorsOverrideStartupGuard.Message)]
    public static partial void CorsDefaultPolicyOverridden(this ILogger logger);

    [LoggerMessage(43, LogLevel.Warning, CorsOverrideStartupGuard.ProviderRegisteredAfterMessage)]
    public static partial void CorsProviderRegisteredAfter(this ILogger logger, string providerType);

    [LoggerMessage(44, LogLevel.Warning, CorsOverrideStartupGuard.CustomProviderWrappedMessage)]
    public static partial void CorsCustomProviderWrapped(this ILogger logger, string providerType);

    // ------------------------------------------------------------------ forwarded headers

    // 50, 51: reserved; do not reuse. An unparseable KnownProxies or KnownNetworks entry fails
    // options validation.

    /// <summary>A trust-set or host problem that fails startup outside Development; the text says which.</summary>
    [LoggerMessage(52, LogLevel.Warning, "{Message}")]
    public static partial void ForwardedHeadersMisconfigured(this ILogger logger, string message);

    // ------------------------------------------------------------------ configuration

    // 60, 61: reserved; do not reuse.

    [LoggerMessage(62, LogLevel.Warning,
        "ApiPipeline.NET 2.0 reads its settings from the '{Section}' section, but this " +
        "configuration still has 1.x top-level sections that are no longer read: [{Sections}]. " +
        "Those features are running on their defaults (rate limiting, CORS, caching and " +
        "compression off). Move each section under '{Section}' with the new name shown. If a " +
        "listed section belongs to your application rather than to ApiPipeline.NET, ignore this.")]
    public static partial void LegacySectionsFound(this ILogger logger, string section, string sections);

    // ------------------------------------------------------------------ request limits

    [LoggerMessage(70, LogLevel.Warning,
        "Request limits: this application runs on {Server}, which does not take these " +
        "ApiPipeline:RequestLimits settings from the application, so they are NOT enforced: " +
        "[{Settings}]. Set the equivalent limits in the server's own configuration (web.config " +
        "requestLimits and limits for IIS, the HTTP.sys registry parameters for HTTP.sys), or run on Kestrel.")]
    public static partial void RequestLimitsNotEnforcedByServer(this ILogger logger, string server, string settings);
}
