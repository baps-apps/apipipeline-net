# ApiPipeline.NET — Production Hardening & Platform Evolution Spec

**Date:** 2026-04-06  
**Author:** Principal Architect Review  
**Status:** Approved for implementation  
**Source:** `2026-04-06-apipipeline-improvements-changes.md`  
**Target:** .NET 10, Kubernetes/cloud-native, 1000+ RPS multi-region

---

## 1. Purpose

This spec covers all changes required to evolve `ApiPipeline.NET` from a capable shared middleware library into a production-hardened, enterprise-grade API platform foundation. It addresses 27 items across security, correctness, performance, observability, and maintainability. Every item from the architecture/code review is captured.

---

## 2. Scope

### In scope
- All 8 critical fixes (C-1 through C-8)
- All 7 should-fix improvements (S-1 through S-7)
- All 7 technical debt items (T-1 through T-7)
- All 5 advanced enhancements (A-1 through A-5)

### Out of scope
- HTTP/2 push, WebSocket middleware
- Auth provider integration (OAuth2, OIDC)
- Service mesh (Istio/Linkerd) integration

---

## 3. Architecture

### 3.1 New Component: `IApiPipelineBuilder` (C-1, A-1, A-5)

The central architectural addition. Replaces the ad-hoc `Use*()` call sequence with a phase-enforced fluent builder.

```
WebApplication.UseApiPipeline(builder => {
    builder
      .WithForwardedHeaders()       // Phase: Infrastructure
      .WithCorrelationId()          // Phase: Infrastructure
      .WithExceptionHandler()       // Phase: Infrastructure
      .WithHttpsRedirection()       // Phase: Infrastructure
      .WithCors()                   // Phase: Security
      .WithAuthentication()         // Phase: Auth
      .WithAuthorization()          // Phase: Auth
      .WithRateLimiting()           // Phase: RateLimiting
      .WithResponseCompression()    // Phase: Output
      .WithResponseCaching()        // Phase: Output
      .WithSecurityHeaders()        // Phase: Headers
      .WithVersionDeprecation()     // Phase: Headers
})
```

**Design decisions:**
- Each `With*()` method records an intent and validates phase constraints
- `Build()` (called internally) applies middleware in phase order regardless of declaration order
- Phase violations throw at build time, not runtime
- Individual `Use*()` methods remain as escape hatches but emit `LogWarning`
- `Skip*()` methods exclude a middleware without breaking phase validation

**Interface contract:**
```csharp
public interface IApiPipelineBuilder
{
    IApiPipelineBuilder WithForwardedHeaders();
    IApiPipelineBuilder WithCorrelationId();
    IApiPipelineBuilder WithExceptionHandler();
    IApiPipelineBuilder WithHttpsRedirection();
    IApiPipelineBuilder WithCors();
    IApiPipelineBuilder WithAuthentication();
    IApiPipelineBuilder WithAuthorization();
    IApiPipelineBuilder WithRateLimiting();
    IApiPipelineBuilder WithResponseCompression();
    IApiPipelineBuilder WithResponseCaching();
    IApiPipelineBuilder WithSecurityHeaders();
    IApiPipelineBuilder WithVersionDeprecation();
    IApiPipelineBuilder SkipHttpsRedirection();
    IApiPipelineBuilder SkipVersionDeprecation();
    // etc.
}
```

**File:** `src/ApiPipeline.NET/Pipeline/IApiPipelineBuilder.cs`  
**File:** `src/ApiPipeline.NET/Pipeline/ApiPipelineBuilder.cs`  
**File:** `src/ApiPipeline.NET/Extensions/WebApplicationExtensions.cs` (add `UseApiPipeline`)

---

### 3.2 New Component: `LiveConfigCorsPolicyProvider` (C-2)

Replaces the static CORS policy built at container start.

```
Request → ICorsPolicyProvider.GetPolicyAsync()
            → IOptionsMonitor<CorsSettings>.CurrentValue
            → Build CorsPolicy from live settings
            → Cache for request lifetime
```

**Design decisions:**
- Policy is re-evaluated per-request from `IOptionsMonitor.CurrentValue`
- No per-request allocation for unchanged settings: compare version stamp before rebuilding
- If `AllowAllInDevelopment = true` and environment is Development, return `AllowAll` policy with `LogWarning`

**File:** `src/ApiPipeline.NET/Cors/LiveConfigCorsPolicyProvider.cs`

---

### 3.3 Modified Component: Kestrel Limits via `IConfigureOptions` (C-4)

Move Kestrel limit configuration out of `WebApplicationBuilderExtensions` and into an `IConfigureOptions<KestrelServerOptions>` registered by `AddRequestLimits`. This ensures validated options are used.

```
AddRequestLimits(services, config)
  → registers IOptions<RequestLimitsOptions> with ValidateOnStart
  → registers IConfigureOptions<KestrelServerOptions> bound to validated options
  → registers IConfigureOptions<FormOptions> bound to validated options
```

`ConfigureKestrelRequestLimits()` is deprecated and will emit a compiler warning, delegating to the options-based path.

**File:** `src/ApiPipeline.NET/Options/ConfigureKestrelOptions.cs` (new)  
**File:** `src/ApiPipeline.NET/Extensions/ServiceCollectionExtensions.cs` (modify `ConfigureRequestLimits`)  
**File:** `src/ApiPipeline.NET/Extensions/WebApplicationBuilderExtensions.cs` (deprecate)

---

### 3.4 Modified Component: Observability (T-1, T-2, T-3, T-4, A-2)

`ApiPipelineTelemetry` is extended with:

| Metric | Type | Dimensions | Replaces |
|---|---|---|---|
| `apipipeline.ratelimit.rejected` | Counter | `policy_name`, `partition_type` | Existing (enhanced) |
| `apipipeline.request.body_bytes` | Histogram | — | New |
| `apipipeline.cors.rejected` | Counter | — | New |
| `apipipeline.cache.hit` | Counter | — | New |
| `apipipeline.cache.miss` | Counter | — | New |
| `apipipeline.security_headers.skipped` | Counter | — | Replaces `applied` |

Remove `SecurityHeadersAppliedCount` (fires every request, no signal).

**File:** `src/ApiPipeline.NET/Observability/ApiPipelineTelemetry.cs`  
**File:** `src/ApiPipeline.NET/Middleware/RequestSizeMiddleware.cs` (new thin middleware)

---

### 3.5 New Package: `ApiPipeline.NET.Versioning` (T-5)

Moves `Asp.Versioning.Mvc` out of the core library.

```
ApiPipeline.NET.csproj          ← no Asp.Versioning.Mvc reference
ApiPipeline.NET.Versioning.csproj  ← references Asp.Versioning.Mvc
                                   ← re-exports ApiVersionDeprecationMiddleware
                                      or provides IApiVersionReader adapter
```

**Option A (preferred):** Define `IApiVersionReader` interface in core. Core middleware calls it via DI. `ApiPipeline.NET.Versioning` registers the `Asp.Versioning`-backed implementation.

**Option B (simpler):** Wrap `ctx.GetRequestedApiVersion()` in a `try/catch` with a fallback to reading from URL segment regex — keeps it in one package but avoids hard dependency.

**Decision:** Option A for clean separation. Option B as fallback if Option A scope is too large for this cycle.

---

### 3.6 New Package: `ApiPipeline.NET.OutputCaching` (T-6, A-4)

Wraps .NET 7+ Output Caching as an alternative to `ResponseCaching`.

```
ResponseCachingSettings.PreferOutputCaching = true
  → UseOutputCache() middleware registered
  → IOutputCacheStore backed by Redis (via ApiPipeline.NET.OutputCaching.Redis satellite)
```

**Design:** New satellite package. No changes to existing `ResponseCachingSettings`/middleware. Consumers opt in. Existing `ResponseCaching` path is unchanged.

---

### 3.7 Modified Component: OWASP API7 Request Validation Hook (A-3)

```csharp
public interface IRequestValidationFilter
{
    ValueTask<RequestValidationResult> ValidateAsync(HttpContext context);
}

public readonly struct RequestValidationResult
{
    public static RequestValidationResult Valid { get; }
    public static RequestValidationResult Invalid(int statusCode, string detail) => ...;
    public bool IsValid { get; }
    public int StatusCode { get; }
    public string? Detail { get; }
}
```

Consumers register implementations via `services.AddRequestValidation<TFilter>()`. The middleware calls them in registration order, short-circuits on first failure, returns RFC 7807 problem details.

**File:** `src/ApiPipeline.NET/Validation/IRequestValidationFilter.cs`  
**File:** `src/ApiPipeline.NET/Middleware/RequestValidationMiddleware.cs`  
**File:** `src/ApiPipeline.NET/Extensions/ServiceCollectionExtensions.cs` (add `AddRequestValidation<T>`)

---

## 4. Detailed Change Specifications

### 4.1 Security Defaults

| Setting | Current Default | New Default | Rationale |
|---|---|---|---|
| `ResponseCompressionSettings.EnableForHttps` | `true` | `false` | BREACH/CRIME risk; opt-in only |
| `CorsSettings.AllowAllInDevelopment` | `true` | `false` | Accidental wildcard CORS in staging |
| `RequestLimitsOptions.MaxRequestBodySize` min | 0 | 1 | Zero-byte body kills all non-GET traffic |
| `RequestLimitsOptions.MaxRequestHeadersTotalSize` min | 0 | 1 | Same issue |
| `RequestLimitsOptions.MaxRequestHeaderCount` min | 0 | 1 | Same issue |
| `RequestLimitsOptions.MaxFormValueCount` min | 0 | 1 | Same issue |

### 4.2 New Validation Rules

- `SunsetLink` must be a valid absolute URI (enforced by `[Url]` annotation + runtime guard)
- `AddCorrelationId()` registers `CorrelationIdMiddleware` explicitly (no longer a no-op)
- `UseApiPipelineExceptionHandler()` guards for presence of `IProblemDetailsService` in DI
- `ForwardLimit` range raised to `[Range(1, 20)]`
- Named rate limit policies: startup log listing all registered policy names

### 4.3 Anonymous Rate Limit Fallback (C-7)

New config property on `RateLimitingOptions`:

```csharp
public enum AnonymousFallbackBehavior { RateLimit, Reject, Allow }

public AnonymousFallbackBehavior AnonymousFallback { get; set; } 
    = AnonymousFallbackBehavior.Reject;
```

Behaviour matrix:

| `RemoteIpAddress` | `AnonymousFallback` | Result |
|---|---|---|
| null | Reject | 429, log warning |
| null | RateLimit | shared `"ip:anonymous"` bucket (legacy) |
| null | Allow | no rate limiting, log warning |
| not null | any | `"ip:<address>"` bucket (unchanged) |

### 4.4 Code Organisation

After refactoring:

```
src/ApiPipeline.NET/
├── Configuration/
│   └── ApiPipelineConfigurationKeys.cs
├── Cors/
│   └── LiveConfigCorsPolicyProvider.cs          ← NEW
├── Middleware/
│   ├── ApiVersionDeprecationMiddleware.cs
│   ├── CorrelationIdMiddleware.cs
│   ├── RequestSizeMiddleware.cs                  ← NEW
│   ├── RequestValidationMiddleware.cs             ← NEW
│   └── SecurityHeadersMiddleware.cs
├── Observability/
│   └── ApiPipelineTelemetry.cs
├── Options/
│   ├── ApiVersionDeprecationOptions.cs
│   ├── ConfigureKestrelOptions.cs                ← NEW (moved from builder ext)
│   ├── ConfigureResponseCachingOptions.cs        ← MOVED from ServiceCollectionExtensions
│   ├── ConfigureResponseCompressionOptions.cs    ← MOVED from ServiceCollectionExtensions
│   ├── CorsSettings.cs
│   ├── ForwardedHeadersSettings.cs
│   ├── RateLimitingOptions.cs
│   ├── RequestLimitsOptions.cs
│   ├── ResponseCachingSettings.cs
│   ├── ResponseCompressionSettings.cs
│   └── SecurityHeadersSettings.cs
├── Pipeline/
│   ├── ApiPipelineBuilder.cs                     ← NEW
│   └── IApiPipelineBuilder.cs                    ← NEW
├── RateLimiting/
│   └── RateLimiterPolicyResolver.cs              ← MOVED from ServiceCollectionExtensions
├── Validation/
│   ├── IRequestValidationFilter.cs               ← NEW
│   └── RequestValidationResult.cs                ← NEW
└── Extensions/
    ├── ServiceCollectionExtensions.cs            (thinned to public API only)
    ├── WebApplicationBuilderExtensions.cs        (ConfigureKestrelRequestLimits deprecated)
    └── WebApplicationExtensions.cs               (UseApiPipeline added)
```

---

## 5. Breaking Changes

| Change | Severity | Migration |
|---|---|---|
| `EnableForHttps` default → `false` | Breaking (opt-in required) | Set `EnableForHttps: true` explicitly if behaviour was relied on |
| `AllowAllInDevelopment` default → `false` | Breaking (dev workflows) | Set `AllowAllInDevelopment: true` explicitly in dev appsettings |
| `MaxRequestBodySize` minimum → 1 | Breaking (0 was invalid anyway) | Fix config; 0 was always a misconfiguration |
| `Asp.Versioning.Mvc` moved to satellite | Breaking | Add `ApiPipeline.NET.Versioning` NuGet reference |
| `ConfigureKestrelRequestLimits` deprecated | Non-breaking (warning) | Remove call; behaviour now automatic via `IConfigureOptions` |
| `SecurityHeadersAppliedCount` removed | Non-breaking (metric disappears) | Remove from dashboards; replace with `security_headers.skipped` |

---

## 6. Test Requirements

Every code change must be accompanied by tests. New test files required:

| Test file | Covers |
|---|---|
| `ApiVersionDeprecationMiddlewareTests.cs` | Deprecation/Sunset headers; SunsetLink injection guard |
| `RequestLimitsTests.cs` | Body size enforcement; MaxRequestBodySize=0 validation rejection |
| `PipelineBuilderTests.cs` | Phase ordering; phase violation detection; skip methods |
| `LiveConfigCorsPolicyProviderTests.cs` | Origin allow/reject; credential+wildcard guard; hot-reload |
| Extend `CorsTests.cs` | AllowAllInDevelopment=false default; CORS rejection counter |
| Extend `ForwardedHeadersTests.cs` | Untrusted proxy spoofing; null RemoteIpAddress fallback |
| Extend `ResponseCompressionTests.cs` | Path exclusion; EnableForHttps=false default |
| Extend `OptionsValidationTests.cs` | All new min=1 range validations |
| Extend `RateLimitingTests.cs` | AnonymousFallback=Reject; partition type dimensions |
| `RequestSizeMiddlewareTests.cs` | Histogram recording; Content-Length absent case |
| `RequestValidationMiddlewareTests.cs` | Valid/invalid filter; multiple filters; ProblemDetails response |

---

## 7. Non-Functional Requirements

- No regression in existing test suite
- No new allocations on the hot path beyond what is explicitly justified
- All new middleware must use `IOptionsMonitor<T>` (not `IOptions<T>`) for hot-reload support
- All new counters/histograms must be registered in `ApiPipelineTelemetry` (single source of metric definitions)
- All breaking default changes must emit `LogWarning` at startup if the old (risky) value is explicitly set

---

## 8. Implementation Order (Priority)

1. **Phase 1 — Critical security & correctness** (C-1 through C-8): All must be done before any new feature work.
2. **Phase 2 — Should-fix** (S-1 through S-7): Code quality, validation guards, operational improvements.
3. **Phase 3 — Technical debt** (T-1 through T-7): Observability, refactoring, test coverage, upgrade paths.
4. **Phase 4 — Advanced** (A-1 through A-5): Pipeline builder, output caching satellite, request validation hook.

---
