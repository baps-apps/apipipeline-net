# Architecture and Pipeline Overview

`ApiPipeline.NET` provides a consistent composition layer for ASP.NET Core middleware and option binding. It standardizes cross-cutting concerns (rate limiting, CORS, security headers, etc.) so consumer services get a hardened pipeline from configuration alone.

## High-level flow

```text
Program.cs
  ── builder.Services.AddApiPipeline(configuration)        // bind options + register services
  ── builder.AddApiPipelineObservability()                  // (optional) OpenTelemetry wiring
  ── builder.Services.AddApiPipelineVersioning(config)      // (optional) Asp.Versioning integration
  ── app.UseApiPipeline(pipeline => pipeline.With*(...))    // middleware composition
  ── app.MapControllers() / app.MapEndpoints(...)           // your routes
```

### Minimal example

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddApiPipeline(builder.Configuration);

var app = builder.Build();

app.UseApiPipeline(pipeline => pipeline
    .WithForwardedHeaders()
    .WithCorrelationId()
    .WithExceptionHandler()
    .WithCors()
    .WithRateLimiting()
    .WithSecurityHeaders());

app.MapGet("/health", () => Results.Ok());
app.Run();
```

## Package layout

The package is intentionally split into independent assemblies:

| Package | Purpose |
|---|---|
| `ApiPipeline.NET` | Core middleware, options, and pipeline builder |
| `ApiPipeline.NET.OpenTelemetry` | Registers `ActivitySource` / `Meter` with OpenTelemetry providers |
| ~~`ApiPipeline.NET.OutputCaching`~~ | Removed — output caching is now built into the core package via `WithOutputCaching()` |
| `ApiPipeline.NET.Versioning` | `IApiVersionReader` implementation backed by `Asp.Versioning` |

Consumer services reference only the packages they need.

## Middleware ordering (phase-enforced)

`UseApiPipeline` applies middleware in a **fixed phase order** regardless of the order you call `With*` methods. This prevents accidental misorderings (e.g., caching before auth):

| Phase | Middleware | Why this position |
|---|---|---|
| 1. Infrastructure | `WithForwardedHeaders()` | Resolve real client IP before anything reads it |
| 2. Infrastructure | `WithCorrelationId()` | Must precede exception handler for error enrichment |
| 3. Infrastructure | `WithExceptionHandler()` | Catch all downstream exceptions as RFC 7807 |
| 4. Infrastructure | `WithHttpsRedirection()` | After forwarded headers for correct scheme detection |
| 5. Security | `WithCors()` | Before auth so preflight isn't rate-limited |
| 6. Auth | `WithAuthentication()` | Establish identity |
| 7. Auth | `WithAuthorization()` | Enforce policies |
| 8. Validation | `WithRequestValidation()` | After auth, before business logic |
| 9. Rate Limiting | `WithRateLimiting()` | After identity is known for per-user partitioning |
| 10. Output | `WithResponseCompression()` | Compress before caching stores compressed form |
| 11. Output | `WithResponseCaching()` | After auth — never cache unauthorized responses; auto-adds `Vary: Origin` when CORS is active |
| 12. Output | `WithOutputCaching()` | Modern caching with distributed store and tag-based invalidation (opt-in) |
| 13. Headers | `WithSecurityHeaders()` | Applied at response time via `OnStarting` |
| 14. Headers | `WithVersionDeprecation()` | Add `Deprecation`/`Sunset`/`Link` headers |

### Skip methods

Use `Skip*` methods to exclude middleware you've already wired manually:

```csharp
app.UseApiPipeline(pipeline => pipeline
    .WithForwardedHeaders()
    .WithCorrelationId()
    .WithExceptionHandler()
    .SkipHttpsRedirection()   // TLS terminated at ingress
    .WithCors()
    .WithAuthentication()
    .WithAuthorization()
    .WithRateLimiting()
    .WithResponseCompression()
    .WithResponseCaching()
    .WithSecurityHeaders()
    .SkipVersionDeprecation());  // not using API versioning
```

## Configuration model

Configuration is option-centric. Each JSON section maps to a strongly-typed options class:

| JSON Section | Options Class | Purpose |
|---|---|---|
| `RateLimitingOptions` | `RateLimitingOptions` | Multi-policy rate limiting |
| `ResponseCompressionOptions` | `ResponseCompressionSettings` | Brotli/Gzip compression |
| `ResponseCachingOptions` | `ResponseCachingSettings` | In-memory response caching |
| `OutputCachingOptions` | `OutputCachingSettings` | ASP.NET Core Output Caching (opt-in migration) |
| `SecurityHeadersOptions` | `SecurityHeadersSettings` | HSTS, CSP, X-Frame-Options, etc. |
| `CorsOptions` | `CorsSettings` | CORS policies |
| `ApiVersionDeprecationOptions` | `ApiVersionDeprecationOptions` | Deprecation/sunset headers |
| `RequestLimitsOptions` | `RequestLimitsOptions` | Kestrel + form body limits |
| `ForwardedHeadersOptions` | `ForwardedHeadersSettings` | Proxy/ingress trust |

Each options class is validated at startup via `ValidateDataAnnotations().ValidateOnStart()`. Invalid configurations fail fast.

### Selective feature registration

Use `ApiPipelineServiceRegistrationOptions` to disable features you don't need:

```csharp
builder.Services.AddApiPipeline(builder.Configuration, options =>
{
    options.AddResponseCaching = false;
    options.AddRequestSizeTracking = false;
});
```

## Key architecture decisions

- **Feature switches in config**: most components can be enabled/disabled via `Enabled` flags without code changes.
- **Safe defaults + startup validation**: invalid production configurations (e.g., wildcard CORS with credentials, no rate limit policies when enabled) fail at startup, not at runtime.
- **Phase-enforced ordering**: middleware runs in a fixed sequence regardless of `With*` call order, preventing auth-bypass bugs from incorrect ordering.
- **Proxy-aware behavior**: forwarded headers are first-class with `EnforceTrustedProxyConfigurationInProduction` to prevent silent fallback to proxy IPs.
- **Composition over magic**: services can use the aggregate `AddApiPipeline` or wire features individually for partial adoption.

## Related docs

- [INTERNALS.md](INTERNALS.md) — implementation details and extension points
- [OPERATIONS.md](OPERATIONS.md) — production deployment guidance
- [RUNBOOK.md](RUNBOOK.md) — incident response procedures
- [TROUBLESHOOTING.md](TROUBLESHOOTING.md) — common issues and diagnostics
- [VERSIONING.md](VERSIONING.md) — versioning and compatibility policy

### Feature deep-dives

Each feature has a dedicated doc with full option reference, examples, and troubleshooting:

- [Rate Limiting](features/rate-limiting.md)
- [Response Compression](features/response-compression.md)
- [Response Caching](features/response-caching.md)
- [Security Headers](features/security-headers.md)
- [CORS](features/cors.md)
- [Correlation IDs](features/correlation-id.md)
- [API Version Deprecation](features/version-deprecation.md)
- [Request Limits](features/request-limits.md)
- [Exception Handling](features/exception-handling.md)
- [Output Caching](features/output-caching.md)
- [Forwarded Headers](features/forwarded-headers.md)
