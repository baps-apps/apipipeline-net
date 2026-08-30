namespace ApiPipeline.NET.Pipeline;

/// <summary>The pipeline phases, declared in the recommended order (Microsoft's middleware order).</summary>
internal enum PipelinePhase
{
    ForwardedHeaders,
    Routing,
    RequestSizeTracking,
    CorrelationId,
    SecurityHeaders,
    RequestTimeouts,
    HttpsRedirection,
    Cors,
    EdgeRateLimiting,
    Authentication,
    Authorization,
    RateLimiting,
    ResponseCompression,
    ResponseCaching,
    OutputCaching,
}

/// <summary>
/// Ordering rules: each says one phase must run before another, and why. A broken rule is logged;
/// a broken <see cref="Rule.Security"/> rule fails startup outside Development.
/// </summary>
internal static class PipelineOrderRules
{
    /// <summary><see cref="First"/> must run before <see cref="Then"/>.</summary>
    /// <param name="First">The earlier phase.</param>
    /// <param name="Then">The later phase.</param>
    /// <param name="Reason">Why.</param>
    /// <param name="Security">Whether breaking it exposes data or defeats a control.</param>
    internal sealed record Rule(PipelinePhase First, PipelinePhase Then, string Reason, bool Security);

    private const string ShortCircuitPhasesReason =
        "responses the later phase produces itself (a redirect, a CORS preflight, 401/403, 429, 504, a cache hit) would not carry ";

    /// <summary>Every rule, in logging order.</summary>
    internal static readonly IReadOnlyList<Rule> All = Build();

    /// <summary>The rules <paramref name="order"/> breaks.</summary>
    internal static IReadOnlyList<Rule> Violations(IReadOnlyList<PipelinePhase> order)
    {
        var position = new Dictionary<PipelinePhase, int>();
        for (var i = 0; i < order.Count; i++)
        {
            position.TryAdd(order[i], i);
        }

        return [.. All.Where(rule =>
            position.TryGetValue(rule.First, out var first)
            && position.TryGetValue(rule.Then, out var then)
            && first > then)];
    }

    private static List<Rule> Build()
    {
        var rules = new List<Rule>();

        void Add(bool security, PipelinePhase first, string reason, PipelinePhase[] then)
        {
            foreach (var phase in then)
            {
                rules.Add(new Rule(first, phase, reason, security));
            }
        }

        void Before(PipelinePhase first, string reason, params PipelinePhase[] then) =>
            Add(security: false, first, reason, then);

        void SecurityBefore(PipelinePhase first, string reason, params PipelinePhase[] then) =>
            Add(security: true, first, reason, then);

        // Rate limits would partition every caller on the proxy's address (one caller exhausts the
        // limit for everyone), and HSTS would never be sent because every request looks like HTTP.
        SecurityBefore(PipelinePhase.ForwardedHeaders,
            "it reads the client address or the request scheme, which forwarded headers restore behind a proxy",
            PipelinePhase.SecurityHeaders, PipelinePhase.EdgeRateLimiting, PipelinePhase.RateLimiting);
        Before(PipelinePhase.ForwardedHeaders,
            "it reads the client address or the request scheme, which forwarded headers restore behind a proxy",
            PipelinePhase.HttpsRedirection, PipelinePhase.Authentication);

        Before(PipelinePhase.Routing,
            "it reads the selected endpoint's metadata ([Authorize], RequireCors, RequireRateLimiting, " +
            "DisableRequestTimeout, CacheOutput, the route template), which only routing provides",
            PipelinePhase.RequestSizeTracking, PipelinePhase.RequestTimeouts, PipelinePhase.Cors,
            PipelinePhase.Authorization, PipelinePhase.RateLimiting, PipelinePhase.OutputCaching);

        var shortCircuiting = new[]
        {
            PipelinePhase.RequestTimeouts, PipelinePhase.HttpsRedirection, PipelinePhase.Cors,
            PipelinePhase.EdgeRateLimiting, PipelinePhase.Authentication, PipelinePhase.Authorization,
            PipelinePhase.RateLimiting, PipelinePhase.ResponseCaching, PipelinePhase.OutputCaching,
        };
        Before(PipelinePhase.CorrelationId, ShortCircuitPhasesReason + "X-Correlation-Id / X-Trace-Id", shortCircuiting);
        Before(PipelinePhase.SecurityHeaders, ShortCircuitPhasesReason + "the security headers", shortCircuiting);

        Before(PipelinePhase.RequestTimeouts,
            "the timeout should also cover token validation",
            PipelinePhase.Authentication);

        SecurityBefore(PipelinePhase.HttpsRedirection,
            "credentials would be processed on a plain-HTTP request that is then redirected",
            PipelinePhase.Authentication, PipelinePhase.Authorization);

        Before(PipelinePhase.Cors,
            "a CORS preflight carries no credentials and must be answered before it can be rejected",
            PipelinePhase.EdgeRateLimiting, PipelinePhase.Authentication, PipelinePhase.Authorization,
            PipelinePhase.RateLimiting);
        Before(PipelinePhase.Cors,
            "a cache hit short-circuits, so a response served from the cache ahead of CORS carries no " +
            "CORS headers and the browser blocks it",
            PipelinePhase.ResponseCaching, PipelinePhase.OutputCaching);

        Before(PipelinePhase.EdgeRateLimiting,
            "it exists to shed floods before they pay for token validation",
            PipelinePhase.Authentication);

        Before(PipelinePhase.Authentication,
            "authorization needs the identity",
            PipelinePhase.Authorization);
        Before(PipelinePhase.Authentication,
            "rate limiting partitions on the authenticated user; without it every caller falls back to IP",
            PipelinePhase.RateLimiting);
        SecurityBefore(PipelinePhase.Authentication,
            "a response cached for an authenticated caller could be served without authentication",
            PipelinePhase.ResponseCaching, PipelinePhase.OutputCaching);

        SecurityBefore(PipelinePhase.Authorization,
            "a response cached for an authorized caller could be served to one who is not authorized",
            PipelinePhase.ResponseCaching, PipelinePhase.OutputCaching);

        Before(PipelinePhase.ResponseCompression,
            "outside the caches, one uncompressed entry is stored and compression follows each " +
            "request's Accept-Encoding",
            PipelinePhase.ResponseCaching, PipelinePhase.OutputCaching);

        return rules;
    }
}
