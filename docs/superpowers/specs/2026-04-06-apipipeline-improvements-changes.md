# ApiPipeline.NET — Complete Changes Catalogue

_Derived from the Principal Architect code & architecture review, April 2026._
_Every item from the review is captured here. Nothing omitted._

---

## How to read this document

Each change has:
- **Category** — Critical / Should-Fix / Tech Debt / Advanced
- **Area** — which part of the codebase is affected
- **Files affected**
- **Problem** — what is wrong and why it matters at scale
- **Change required** — the concrete thing that must change

---

## CRITICAL — Must Fix Before Production Scale

---

### C-1 — No Pipeline Ordering Enforcement

**Category:** Critical Architecture  
**Area:** Pipeline composition  
**Files:** `src/ApiPipeline.NET/Extensions/WebApplicationExtensions.cs`, new file

**Problem:** Middleware order is only enforced by documentation and sample code. Consumers can call `UseResponseCaching()` before `UseAuthentication()`, causing auth-bypass via cached responses. `PipelineOrderingTests` proves the risk exists but only guards the sample — every consuming microservice starts fresh with no guardrails.

**Change required:**
- Introduce `IApiPipelineBuilder` fluent builder that registers middleware in a fixed, verified-safe order internally
- Expose `UseApiPipeline(Action<IApiPipelineBuilder> configure)` extension on `WebApplication`
- Builder phases: `Infrastructure → Security → Auth → RateLimiting → Output → Headers`
- Each `With*()` method records an intent; `Build()` applies middleware in correct phase order
- Keep individual `Use*()` methods as escape hatches but document they are advanced/unsafe

---

### C-2 — CORS Policy Is Baked at Container Start (Not Hot-Reloadable)

**Category:** Critical Architecture  
**Area:** CORS  
**Files:** `src/ApiPipeline.NET/Extensions/ServiceCollectionExtensions.cs` (ConfigureCors)

**Problem:** `ConfigureCors` binds `CorsSettings` directly into a local variable and passes it to `services.AddCors(...)` at DI build time. The policy lambda is a closure over that snapshot — config reloads via `IOptionsMonitor` are validated but never applied to the live CORS policy. Origin changes require a full pod recycle.

**Change required:**
- Implement `LiveConfigCorsPolicyProvider : ICorsPolicyProvider`
- Reads `IOptionsMonitor<CorsSettings>.CurrentValue` per-request
- Register as `services.AddSingleton<ICorsPolicyProvider, LiveConfigCorsPolicyProvider>()`
- Remove static policy registration from `ConfigureCors`; keep only options binding + validation

---

### C-3 — Named Rate Limit Policies Are Frozen at Startup

**Category:** Critical Architecture  
**Area:** Rate limiting  
**Files:** `src/ApiPipeline.NET/Extensions/ServiceCollectionExtensions.cs` (ConfigureRateLimiting lines 219–239)

**Problem:** Named policies are registered by iterating `configuredOptions.Policies` at DI build time via `.Get<RateLimitingOptions>()`. New policies added to appsettings after startup are picked up by `IOptionsMonitor` for the global limiter but never registered in `RateLimiterOptions` for per-endpoint `RequireRateLimiting("newPolicyName")`, which throws `InvalidOperationException` at runtime.

**Change required:**
- Document clearly that named policy registration is startup-time only
- Add startup validation that every policy name in `DefaultPolicy` and any endpoint-level policy references exist in the registered set
- Long-term: explore `IRateLimiterPolicy<TResource>` factory pattern to enable post-startup policy names (document as a future enhancement if not implemented in this cycle)
- Emit a `LogWarning` on startup listing all registered named policy names so operators can verify

---

### C-4 — `ConfigureKestrelRequestLimits` Bypasses `ValidateOnStart`

**Category:** Critical Code  
**Area:** Request limits / Kestrel  
**Files:** `src/ApiPipeline.NET/Extensions/WebApplicationBuilderExtensions.cs`

**Problem:** `ConfigureKestrelRequestLimits` reads configuration directly via `builder.Configuration.GetSection(...).Bind(requestLimits)` before `ValidateOnStart` fires. Invalid values (e.g., `MaxRequestBodySize: -5`) pass into Kestrel without rejection. If `AddRequestLimits` is never called, a silent no-op occurs with no warning.

**Change required:**
- Move Kestrel limit configuration into an `IConfigureOptions<KestrelServerOptions>` registered inside `AddRequestLimits`:
  ```csharp
  services.AddOptions<KestrelServerOptions>()
      .Configure<IOptions<RequestLimitsOptions>>((kestrel, limits) => {
          if (limits.Value.Enabled) ApplyLimits(limits.Value, kestrel.Limits);
      });
  ```
- Deprecate or remove `ConfigureKestrelRequestLimits` builder extension
- Add guard: if `AddRequestLimits` was not called but `ConfigureKestrelRequestLimits` is called, throw `InvalidOperationException`

---

### C-5 — `MaxRequestBodySize = 0` Is Silently Accepted

**Category:** Critical Code  
**Area:** Request limits validation  
**Files:** `src/ApiPipeline.NET/Options/RequestLimitsOptions.cs`

**Problem:** `[Range(0, long.MaxValue)]` allows `0`, meaning zero bytes max body size — every POST/PUT/PATCH is rejected with 413. Indistinguishable from intentional configuration in logs.

**Change required:**
- Change to `[Range(1, long.MaxValue)]` on `MaxRequestBodySize`
- Same fix for `MaxRequestHeadersTotalSize`, `MaxRequestHeaderCount`, `MaxFormValueCount` — all currently allow 0

---

### C-6 — `AddCorrelationId()` Is a No-Op with Misleading Symmetry

**Category:** Critical Code  
**Area:** Correlation ID  
**Files:** `src/ApiPipeline.NET/Extensions/ServiceCollectionExtensions.cs`

**Problem:** `AddCorrelationId()` returns immediately without registering anything. Consumers believe they've configured something. In test harnesses without full ASP.NET Core DI, logger injection silently fails.

**Change required:**
- Either: register `CorrelationIdMiddleware` explicitly via `services.TryAddSingleton<CorrelationIdMiddleware>()` and add startup guard
- Or: remove the method entirely and update all docs/samples to not call it
- Add XML doc note explaining what it does (or doesn't do)

---

### C-7 — Anonymous Rate Limit Fallback Is a Single Shared Global Bucket

**Category:** Critical Code  
**Area:** Rate limiting  
**Files:** `src/ApiPipeline.NET/Extensions/ServiceCollectionExtensions.cs` (GetPartitionKey)

**Problem:** When `RemoteIpAddress` is null (misconfigured forwarded headers), all anonymous traffic collapses into `"ip:anonymous"`. A single client can exhaust this bucket, causing a full denial of service for all anonymous users. At 1000 RPS with 60% anonymous traffic, this is a realistic production outage scenario.

**Change required:**
- Add `AnonymousFallback` configuration option: `RateLimit | Reject | Allow`
- Default: `Reject` (return 429 immediately with a log warning on the first occurrence per minute)
- When `RemoteIpAddress` is null, log a `LogWarning` on the first occurrence (rate-limited to avoid log flooding)
- Update XML doc comment on `GetPartitionKey` to document the fallback behaviour

---

### C-8 — HTTPS Compression Defaults to Enabled (BREACH/CRIME Risk)

**Category:** Critical Code  
**Area:** Response compression  
**Files:** `src/ApiPipeline.NET/Options/ResponseCompressionSettings.cs`

**Problem:** `EnableForHttps = true` is the default. BREACH/CRIME attacks exploit HTTPS+compression when attacker-controlled content and secrets share a compressed response. For API platforms where user query parameters land in the same response as tokens, the risk is real. Consumers who don't read the comment get the dangerous setting.

**Change required:**
- Change default to `EnableForHttps = false`
- Expand the XML doc comment to list specific threat scenarios (BREACH, CRIME)
- Add a startup log message at `LogWarning` level when `EnableForHttps = true` is explicitly configured, noting the risk

---

## SHOULD FIX — Material Improvements

---

### S-1 — `ServiceCollectionExtensions.cs` Exceeds Single Responsibility

**Category:** Should Fix / Maintainability  
**Area:** Code organisation  
**Files:** `src/ApiPipeline.NET/Extensions/ServiceCollectionExtensions.cs`

**Problem:** At 563 lines, the file contains public extension methods, internal configuration helpers, `RateLimiterPolicyResolver`, `ConfigureResponseCompressionOptions`, and `ConfigureResponseCachingOptions`. Growing harder to navigate; onboarding cost increases.

**Change required:**
- Move `RateLimiterPolicyResolver` → `src/ApiPipeline.NET/RateLimiting/RateLimiterPolicyResolver.cs`
- Move `ConfigureResponseCompressionOptions` → `src/ApiPipeline.NET/Options/ConfigureResponseCompressionOptions.cs`
- Move `ConfigureResponseCachingOptions` → `src/ApiPipeline.NET/Options/ConfigureResponseCachingOptions.cs`
- Keep `ServiceCollectionExtensions.cs` as public API surface only (public extension methods)

---

### S-2 — Per-Request `Dictionary<string, object>` Allocation in CorrelationIdMiddleware

**Category:** Should Fix / Performance  
**Area:** Correlation ID middleware  
**Files:** `src/ApiPipeline.NET/Middleware/CorrelationIdMiddleware.cs`

**Problem:** `new Dictionary<string, object> { ["CorrelationId"] = correlationId }` allocates a new dictionary per request. At 1000 RPS = 1000 heap allocations/second; GC pressure accumulates under sustained load.

**Change required:**
- Replace with `new[] { new KeyValuePair<string, object?>("CorrelationId", (object?)correlationId) }`
- Or use the `ILogger.BeginScope(string, T)` overload: `_logger.BeginScope("{CorrelationId}", correlationId)`

---

### S-3 — `UseResponseCompression` Snapshots Options at Build Time

**Category:** Should Fix / Consistency  
**Area:** Response compression  
**Files:** `src/ApiPipeline.NET/Extensions/WebApplicationExtensions.cs`

**Problem:** `var settings = app.Services.GetRequiredService<IOptions<...>>().Value` is called once at pipeline build time. `ExcludedPaths` and `Enabled` won't reflect config reloads. Inconsistent with `SecurityHeadersMiddleware` which uses `IOptionsMonitor.CurrentValue` per-request.

**Change required:**
- Inject `IOptionsMonitor<ResponseCompressionSettings>` into the `UseWhen` predicate
- Pre-compute `PathString[]` only when the monitored value changes (use `OnChange` callback to invalidate a cached array)

---

### S-4 — `AllowAllInDevelopment = true` Is a Deployment Time Bomb

**Category:** Should Fix / Security  
**Area:** CORS  
**Files:** `src/ApiPipeline.NET/Options/CorsSettings.cs`, `src/ApiPipeline.NET/Extensions/WebApplicationExtensions.cs`

**Problem:** `ASPNETCORE_ENVIRONMENT=Development` is frequently set in staging and CI environments. With `AllowAllInDevelopment = true` as the default, any such environment exposes wildcard CORS with no warning.

**Change required:**
- Change default to `AllowAllInDevelopment = false`
- Emit `LogWarning` whenever the `AllowAll` policy is activated: `"CORS: AllowAll policy is active. All origins are allowed. Do not use in production."`

---

### S-5 — `SunsetLink` Is Not Validated as a URL (Potential Header Injection)

**Category:** Should Fix / Security  
**Area:** API version deprecation  
**Files:** `src/ApiPipeline.NET/Options/ApiVersionDeprecationOptions.cs`, `src/ApiPipeline.NET/Middleware/ApiVersionDeprecationMiddleware.cs`

**Problem:** `SunsetLink` is written directly into a `Link` response header without URL validation. A value like `"foo>\nX-Injected: evil"` would inject an arbitrary response header.

**Change required:**
- Add `[Url]` data annotation to `SunsetLink`
- Add runtime guard in `ApiVersionDeprecationMiddleware`: validate with `Uri.TryCreate(link, UriKind.Absolute, out _)` before setting the header; log warning and skip if invalid

---

### S-6 — `UseApiPipelineExceptionHandler` Has No Guard for Missing `AddApiPipelineExceptionHandler`

**Category:** Should Fix / Developer Experience  
**Area:** Exception handling  
**Files:** `src/ApiPipeline.NET/Extensions/WebApplicationExtensions.cs`

**Problem:** If `UseApiPipelineExceptionHandler()` is called without `AddApiPipelineExceptionHandler()`, `IProblemDetailsService` is not in DI. `UseStatusCodePages()` silently falls back to plain-text responses with no diagnostic path.

**Change required:**
- Add guard at the top of `UseApiPipelineExceptionHandler`:
  ```csharp
  if (app.Services.GetService<IProblemDetailsService>() is null)
      throw new InvalidOperationException(
          "UseApiPipelineExceptionHandler requires AddApiPipelineExceptionHandler to be called during service registration.");
  ```

---

### S-7 — `ForwardLimit` Validation Cap of 10

**Category:** Should Fix / Operational  
**Area:** Forwarded headers  
**Files:** `src/ApiPipeline.NET/Options/ForwardedHeadersSettings.cs`

**Problem:** `[Range(1, 10)]` — CloudFront → WAF → ALB → Nginx Ingress → Pod = 4 hops minimum in AWS. Complex multi-region topologies with service mesh sidecars can exceed 10. The error message is not helpful.

**Change required:**
- Raise range to `[Range(1, 20)]`
- Add XML doc comment with example topologies and their hop counts

---

## TECHNICAL DEBT

---

### T-1 — Missing Observability: Rate Limit Partition Dimensions

**Category:** Tech Debt / Observability  
**Area:** Telemetry  
**Files:** `src/ApiPipeline.NET/Observability/ApiPipelineTelemetry.cs`, `src/ApiPipeline.NET/Extensions/ServiceCollectionExtensions.cs`

**Problem:** `apipipeline.ratelimit.rejected` has no dimensions. Operators cannot distinguish "one user is hammering" from "all users are getting throttled." Useless for SRE alerting.

**Change required:**
- Add `KeyValuePair` dimensions to `RecordRateLimitRejected`: `policy_name` and `partition_type` (`user|ip|anonymous|unknown`)
- Update signature: `RecordRateLimitRejected(string policyName, string partitionType)`

---

### T-2 — Missing Observability: Request Body Size Histogram

**Category:** Tech Debt / Observability  
**Area:** Telemetry  
**Files:** `src/ApiPipeline.NET/Observability/ApiPipelineTelemetry.cs`

**Problem:** No metric for request body sizes. Cannot detect payload growth (memory pressure signal) or validate that request limits are actually being enforced in practice.

**Change required:**
- Add `Histogram<long> RequestBodyBytesHistogram` to `ApiPipelineTelemetry`
- Record in a `RequestSizeMiddleware` (thin middleware placed early in pipeline, after forwarded headers)
- Only sample when `Content-Length` header is present (avoid streaming overhead)

---

### T-3 — Missing Observability: Cache Hit/Miss, CORS Rejection Counters

**Category:** Tech Debt / Observability  
**Area:** Telemetry  
**Files:** `src/ApiPipeline.NET/Observability/ApiPipelineTelemetry.cs`

**Problem:** No visibility into response cache effectiveness or CORS rejection rate. In production, a misconfigured cache or CORS policy is invisible without these counters.

**Change required:**
- Add `Counter<long> CorsRejectedCount` — increment in `LiveConfigCorsPolicyProvider` when origin is rejected
- Add `Counter<long> ResponseCacheHitCount` and `Counter<long> ResponseCacheMissCount` — wrap `IResponseCache` or use `ResponseCachingContext` feature if available

---

### T-4 — `SecurityHeadersAppliedCount` Is a Noisy Low-Signal Metric

**Category:** Tech Debt / Observability  
**Area:** Telemetry  
**Files:** `src/ApiPipeline.NET/Middleware/SecurityHeadersMiddleware.cs`, `src/ApiPipeline.NET/Observability/ApiPipelineTelemetry.cs`

**Problem:** Fires on every successful response. At 1000 RPS this consumes metric cardinality budget and produces a monotonically increasing counter that adds no diagnostic signal.

**Change required:**
- Remove `ApiPipelineTelemetry.RecordSecurityHeadersApplied()` call from `SecurityHeadersMiddleware`
- Remove `SecurityHeadersAppliedCount` counter from `ApiPipelineTelemetry`
- Replace with a counter that fires only on error: `SecurityHeadersSkippedCount` (fires when `settings.Enabled = false` or an exception occurs during header application)

---

### T-5 — `Asp.Versioning.Mvc` Is a Core Library Dependency

**Category:** Tech Debt / Upgrade Risk  
**Area:** Project dependencies  
**Files:** `src/ApiPipeline.NET/ApiPipeline.NET.csproj`

**Problem:** Forces all consumers to take `Asp.Versioning.Mvc` (and its transitive dependencies) even if they use a different versioning strategy, URL-based prefix routing without versioning, or gRPC. This is a silent coupling tax.

**Change required:**
- Move `Asp.Versioning.Mvc` reference to an optional satellite package: `ApiPipeline.NET.Versioning`
- `ApiVersionDeprecationMiddleware.GetRequestedApiVersion()` call must be made conditional (compile-time optional) or abstracted behind an `IApiVersionReader` interface in the core package
- Update sample to reference the satellite package

---

### T-6 — `ResponseCaching` Middleware vs. Output Caching — Migration Path

**Category:** Tech Debt / Upgrade Risk  
**Area:** Response caching  
**Files:** `src/ApiPipeline.NET/Extensions/ServiceCollectionExtensions.cs`, `src/ApiPipeline.NET/Extensions/WebApplicationExtensions.cs`

**Problem:** `ResponseCaching` middleware is in-memory per-pod only. At multi-region scale, pod-local caches provide no benefit — each pod has a cold cache after rolling deploys. `IOutputCacheStore` (available since .NET 7) supports Redis-backed distributed caching, cache eviction by tag, and per-endpoint revalidation semantics.

**Change required:**
- Add `ResponseCachingSettings.PreferOutputCaching` boolean (default `false` for backwards compat)
- When `true`, register and use Output Caching middleware instead
- Document migration path to Output Caching in XML docs and README
- Add satellite package `ApiPipeline.NET.OutputCaching` wrapping the new path

---

### T-7 — Test Coverage Gaps for Security-Critical Paths

**Category:** Tech Debt / Test coverage  
**Area:** Tests  
**Files:** `tests/ApiPipeline.NET.Tests/`

**Problem:** No tests for:
- `ConfigureKestrelRequestLimits` body size enforcement
- `ApiVersionDeprecationMiddleware` (Deprecation/Sunset headers, SunsetLink injection guard)
- CORS origin enforcement (allowed vs. rejected origins, `AllowAllInDevelopment` flag)
- `ForwardedHeaders` trust chain (trusted vs. untrusted proxy spoofing)
- `UseResponseCompression` path exclusion
- `MaxRequestBodySize = 0` validation rejection

**Change required:**
- Add `ApiVersionDeprecationMiddlewareTests.cs`
- Add `RequestLimitsTests.cs`
- Extend `CorsTests.cs` with origin-rejection and credential+wildcard cases
- Extend `ForwardedHeadersTests.cs` with untrusted proxy spoofing scenario
- Extend `ResponseCompressionTests.cs` with excluded path tests
- Extend `OptionsValidationTests.cs` with `MaxRequestBodySize = 0` case

---

## ADVANCED — Enterprise Platform Evolution

---

### A-1 — Add `UseApiPipeline` All-In-One Ordered Extension

**Category:** Advanced  
**Area:** Pipeline composition  
**Files:** `src/ApiPipeline.NET/Extensions/WebApplicationExtensions.cs`

**Description:**
```csharp
app.UseApiPipeline(options => options
    .SkipHttpsRedirection()   // for internal cluster services
    .SkipVersionDeprecation()
);
```
Applies all middleware in the verified-safe order. Consumers get correctness by default.

---

### A-2 — Per-Policy Rate Limit Metrics with Partition Dimensions

**Category:** Advanced (partially overlaps T-1)  
**Area:** Telemetry  
**Files:** `src/ApiPipeline.NET/Observability/ApiPipelineTelemetry.cs`

See T-1. This is the full realisation: dimensions enable SRE alerting on per-user vs. per-IP vs. global exhaustion.

---

### A-3 — OWASP API7 Request Validation Hook

**Category:** Advanced  
**Area:** New feature  
**Files:** New: `src/ApiPipeline.NET/Validation/IRequestValidationFilter.cs`

**Description:** Expose an `IRequestValidationFilter` extension point so consumers can plug in model-level or schema-level request validation at the pipeline layer without duplicating it across every controller. Prevents the package from being treated as complete security coverage.

---

### A-4 — Output Caching Satellite Package

See T-6. Full satellite implementation with Redis store support.

---

### A-5 — `IApiPipelineBuilder` Phase-Based Composition

See C-1. Full realisation with phase enum, ordered registration, and conflict detection.

---

## Change Count Summary

| Priority | Count |
|---|---|
| Critical (C-1 through C-8) | 8 |
| Should Fix (S-1 through S-7) | 7 |
| Technical Debt (T-1 through T-7) | 7 |
| Advanced (A-1 through A-5) | 5 |
| **Total** | **27** |
