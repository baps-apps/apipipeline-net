# ApiPipeline.NET

[![.NET 10](https://img.shields.io/badge/.NET-10-512BD4)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

Shared ASP.NET Core API pipeline that centralizes request hardening, operational defaults, and production-safe middleware order.

Core capabilities:

- Rate limiting (multi-policy + named policies + API key partitioning + `X-RateLimit-*` informational headers)
- Response compression, response caching, and output caching (distributed, tag-based)
- Security headers (HSTS, `X-Content-Type-Options`, `Referrer-Policy`, `X-Frame-Options`, `Content-Security-Policy`, `Permissions-Policy`)
- CORS (dev-friendly and production-restrictive modes, auto `Vary: Origin` with caching)
- Correlation IDs with injection-safe validation
- API version deprecation headers (`Deprecation`, `Sunset`, `Link`)
- Kestrel + form request limits
- Structured RFC 7807 exception handling
- Forwarded headers for proxy/ingress deployments

Consumer services should reference the **ApiPipeline.NET NuGet package**, not a project reference.

## Table of contents

- [Quick start](#quick-start)
- [Benefits](#benefits)
- [What it configures](#what-it-configures)
- [Installation](#installation)
- [Configuration](#configuration)
- [Program.cs wiring](#programcs-wiring)
- [Security](#security)
- [Production checklist](#production-checklist)
- [Observability](#observability)
- [Rate limiting in production](#rate-limiting-in-production)
- [Versioning and compatibility](#versioning-and-compatibility)
- [Pairing with HTTP client resilience](#pairing-with-http-client-resilience)
- [Extended docs](#extended-docs)

## Quick start

Get a hardened API pipeline in under 10 lines:

```csharp
using ApiPipeline.NET.Extensions;

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

Add configuration in `appsettings.json` — see [Configuration](#configuration) for full examples.

## Benefits

This package is primarily a **standardization + maintenance win** for ASP.NET Core APIs:

- **One pipeline, many apps**: consistent rate limiting, compression, caching, CORS, security headers, and deprecation headers across all services.
- **Fewer bugs and regressions**: behavior changes are implemented **once** in ApiPipeline.NET instead of being hand-rolled in each repo.
- **Faster onboarding**: new APIs get a hardened pipeline by:
  - Adding the package
  - Adding configuration sections
  - Calling the per-feature `Add`*/`Use`* extensions (Kestrel limits apply automatically when you call `AddRequestLimits`).
- **Safer operations**: many behaviors are feature-flagged in config (e.g. disable rate limiting or security headers) without code changes.
- **Shared Kestrel limits**: request/body/header limits come from `RequestLimitsOptions` so they are consistent across services.
- **Phase-enforced ordering**: middleware always runs in the correct sequence regardless of `With*` call order, preventing auth-bypass bugs.

## What it configures

ApiPipeline.NET provides:

- **Rate limiting**
  - **Options**: `RateLimitingOptions` with named policies built on `System.Threading.RateLimiting` (FixedWindow, SlidingWindow, Concurrency, TokenBucket).
  - **Registration**: `AddRateLimiting(builder.Configuration)`.
  - **Middleware**: `UseRateLimiting()` emits RFC 7807 ProblemDetails (`429`) with `Retry-After` when available and `Cache-Control: no-store`.
  - **Informational headers**: when `EmitRateLimitHeaders` is `true` (default), every response includes `X-RateLimit-Limit` and `X-RateLimit-Reset` for client-side adaptive backoff.
  - **API key partitioning**: set `EnableApiKeyPartitioning: true` to partition rate limits by `X-Api-Key` header for machine-to-machine traffic without full authentication.
- **Response compression**
  - **Options**: `ResponseCompressionSettings` – enable/disable, Brotli/Gzip providers, MIME types to compress, and paths to exclude.
  - **Registration**: `AddResponseCompression(builder.Configuration)` configures ASP.NET Core `AddResponseCompression` based on those settings.
  - **Middleware**: `UseResponseCompression()` compresses eligible responses, skipping any paths listed in `ExcludedPaths` (e.g. `/health`).
- **Response caching**
  - **Options**: `ResponseCachingSettings` – global cache size limit, case-sensitive paths, and `PreferOutputCaching` migration hint.
  - **Registration**: `AddResponseCaching(builder.Configuration)` registers `AddResponseCaching` with the configured limits.
  - **Middleware**: `UseResponseCaching()` enables ASP.NET Core response caching so cacheable responses can be served without recomputing them. When CORS is also enabled, `Vary: Origin` is automatically appended to prevent cross-origin cache poisoning.
- **Output caching** (opt-in migration from response caching)
  - **Options**: `OutputCachingSettings` with `Enabled` flag (default: `false`).
  - **Registration**: `AddOutputCaching(builder.Configuration)` or via `AddApiPipeline` with `options.AddOutputCaching = true`.
  - **Middleware**: `UseOutputCaching()` / `WithOutputCaching()` enables ASP.NET Core Output Caching with distributed store support (Redis), tag-based eviction, and per-endpoint policies.
- **Security headers**
  - **Options**: `SecurityHeadersSettings` – HSTS (`Strict-Transport-Security` with preload), `Referrer-Policy`, `X-Content-Type-Options: nosniff`, `X-Frame-Options`, `Content-Security-Policy`, and `Permissions-Policy`.
  - **Registration**: `AddSecurityHeaders(builder.Configuration)` binds those settings.
  - **Middleware**: `SecurityHeadersMiddleware` + `UseSecurityHeaders()` apply all configured security headers via `OnStarting` callbacks so they are set even when downstream components write directly to the response. HSTS is automatically skipped in Development environments.
- **CORS**
  - **Options**: `CorsSettings` – enable/disable, dev‑only allow‑all flag, allowed origins/methods/headers, and whether credentials are allowed.
  - **Registration**: `AddCors(builder.Configuration)` registers CORS policies from configuration.
  - **Middleware**: `UseCors()` uses an allow‑all policy in development (when configured) or a restrictive policy based on `AllowedOrigins`, `AllowedMethods`, and `AllowedHeaders` in other environments.
  - **Defaults**: `AllowedMethods` defaults to `["GET", "POST", "PUT", "PATCH", "DELETE", "OPTIONS"]`. `AllowedHeaders` defaults to `["Content-Type", "Authorization", "X-Correlation-Id"]` (set to `["*"]` explicitly if unrestricted headers are needed).
- **Correlation ID**
  - **Extensions**: `.AddCorrelationId()` registers the middleware; `.UseCorrelationId()` adds it to the ASP.NET Core pipeline.
  - **Behavior**: `CorrelationIdMiddleware` reads an incoming `X-Correlation-Id` header, validates it against a strict pattern (alphanumeric, hyphens, underscores, dots, max 128 chars) to prevent header injection, and echoes it back. Invalid or missing IDs are replaced with a server-generated value (from the current `Activity.TraceId` or a new GUID). The ID is stored in `HttpContext.Items["X-Correlation-Id"]` for downstream use.
- **API version deprecation**
  - **Options**: `ApiVersionDeprecationOptions` – list of deprecated API versions with optional deprecation/sunset dates and documentation links. The `PathPrefix` property (default `/api`) controls which routes are inspected.
  - **Registration**: `AddApiVersionDeprecation(builder.Configuration)` binds those options.
  - **Middleware**: `ApiVersionDeprecationMiddleware` + `UseApiVersionDeprecation()` add `Deprecation`, `Sunset`, and `Link` headers for matching API versions on routes starting with the configured `PathPrefix`.
- **Kestrel and form request limits**
  - **Options**: `RequestLimitsOptions` – maximum body size, total header size, header count, and maximum form value count.
  - **Registration**: `AddRequestLimits(builder.Configuration)` binds limits, registers `IConfigureOptions<KestrelServerOptions>` so Kestrel applies the same ceilings to all HTTP traffic, and configures ASP.NET Core `FormOptions` for form and multipart requests.

## Installation

### Step 1: Authenticate with your NuGet feed

If you publish ApiPipeline.NET to GitHub Packages (example), add the source:

```bash
dotnet nuget add source https://nuget.pkg.github.com/baps-apps/index.json \
  --name github \
  --username YOUR_GITHUB_USERNAME \
  --password YOUR_GITHUB_PAT \
  --store-password-in-clear-text
```

Create a PAT at `https://github.com/settings/tokens` with `read:packages` permission.

Or use whatever feed you host the `ApiPipeline.NET` package on.

### Step 2: Install packages

```bash
dotnet add package ApiPipeline.NET --source github
```

For OpenTelemetry observability (tracing, metrics, structured logging), add the optional package:

```bash
dotnet add package ApiPipeline.NET.OpenTelemetry --source github
```

Then call `builder.AddApiPipelineObservability()` in your `Program.cs` (see [Observability](#observability)).

Consumers reference the **ApiPipeline.NET** NuGet package, not a project reference.

## Configuration

ApiPipeline.NET expects strongly-typed options bound from configuration sections. Below is a **complete development** `appsettings.json` showing every available property:

```json
{
  "RateLimitingOptions": {
    "Enabled": true,
    "AnonymousFallback": "Reject",
    "DefaultPolicy": "strict",
    "Policies": [
      {
        "Name": "strict",
        "Kind": "FixedWindow",
        "PermitLimit": 20,
        "WindowSeconds": 60,
        "QueueLimit": 2,
        "QueueProcessingOrder": "OldestFirst",
        "AutoReplenishment": true
      },
      {
        "Name": "permissive",
        "Kind": "SlidingWindow",
        "PermitLimit": 200,
        "WindowSeconds": 60,
        "SegmentsPerWindow": 6,
        "QueueLimit": 0,
        "QueueProcessingOrder": "OldestFirst",
        "AutoReplenishment": true
      },
      {
        "Name": "burst-tolerant",
        "Kind": "TokenBucket",
        "PermitLimit": 50,
        "WindowSeconds": 30,
        "TokensPerPeriod": 10,
        "QueueLimit": 5,
        "QueueProcessingOrder": "OldestFirst",
        "AutoReplenishment": true
      }
    ]
  },
  "ResponseCompressionOptions": {
    "Enabled": true,
    "EnableForHttps": true,
    "EnableBrotli": true,
    "EnableGzip": true,
    "MimeTypes": [
      "application/json",
      "application/problem+json"
    ],
    "ExcludedMimeTypes": [],
    "ExcludedPaths": ["/health"]
  },
  "ResponseCachingOptions": {
    "Enabled": true,
    "SizeLimitBytes": 104857600,
    "UseCaseSensitivePaths": false,
    "PreferOutputCaching": false
  },
  "OutputCachingOptions": {
    "Enabled": false
  },
  "SecurityHeadersOptions": {
    "Enabled": true,
    "ReferrerPolicy": "no-referrer",
    "AddXContentTypeOptionsNoSniff": true,
    "EnableStrictTransportSecurity": true,
    "StrictTransportSecurityMaxAgeSeconds": 31536000,
    "StrictTransportSecurityIncludeSubDomains": true,
    "StrictTransportSecurityPreload": false,
    "AddXFrameOptions": true,
    "XFrameOptionsValue": "DENY",
    "ContentSecurityPolicy": null,
    "PermissionsPolicy": null
  },
  "CorsOptions": {
    "Enabled": true,
    "AllowAllInDevelopment": true,
    "AllowedOrigins": ["https://myapp.com"],
    "AllowedMethods": ["GET", "POST", "PUT", "PATCH", "DELETE", "OPTIONS"],
    "AllowedHeaders": ["Content-Type", "Authorization", "X-Correlation-Id"],
    "AllowCredentials": true
  },
  "ApiVersionDeprecationOptions": {
    "Enabled": true,
    "PathPrefix": "/api",
    "DeprecatedVersions": [
      {
        "Version": "1.0",
        "DeprecationDate": "2025-07-01T00:00:00Z",
        "SunsetDate": "2026-07-01T00:00:00Z",
        "SunsetLink": "https://myapp.com/docs/api-v1-deprecation"
      }
    ]
  },
  "RequestLimitsOptions": {
    "Enabled": true,
    "MaxRequestBodySize": 10485760,
    "MaxRequestHeadersTotalSize": 32768,
    "MaxRequestHeaderCount": 100,
    "MaxFormValueCount": 1024
  },
  "ForwardedHeadersOptions": {
    "Enabled": true,
    "ForwardLimit": 1,
    "KnownProxies": [],
    "KnownNetworks": [],
    "ClearDefaultProxies": false,
    "EnforceTrustedProxyConfigurationInProduction": true,
    "SuppressServerHeader": true
  }
}
```

Configuration keys are defined in `ApiPipeline.NET.Configuration.ApiPipelineConfigurationKeys` for reuse.

For **production** (for example, an API behind Kubernetes ingress), you will usually want stricter defaults:

```json
{
  "RateLimitingOptions": {
    "Enabled": true,
    "AnonymousFallback": "Reject",
    "EmitRateLimitHeaders": true,
    "EnableApiKeyPartitioning": true,
    "DefaultPolicy": "per-user-burst",
    "Policies": [
      {
        "Name": "per-user-burst",
        "Kind": "FixedWindow",
        "PermitLimit": 100,
        "WindowSeconds": 60,
        "QueueLimit": 0,
        "QueueProcessingOrder": "OldestFirst",
        "AutoReplenishment": true
      }
    ]
  },
  "ResponseCompressionOptions": {
    "Enabled": true,
    "EnableForHttps": true,
    "EnableBrotli": true,
    "EnableGzip": true,
    "MimeTypes": [
      "application/json",
      "application/problem+json"
    ],
    "ExcludedMimeTypes": [],
    "ExcludedPaths": ["/health"]
  },
  "ResponseCachingOptions": {
    "Enabled": true,
    "SizeLimitBytes": 104857600,
    "UseCaseSensitivePaths": false,
    "PreferOutputCaching": true
  },
  "OutputCachingOptions": {
    "Enabled": true
  },
  "SecurityHeadersOptions": {
    "Enabled": true,
    "ReferrerPolicy": "no-referrer",
    "AddXContentTypeOptionsNoSniff": true,
    "EnableStrictTransportSecurity": true,
    "StrictTransportSecurityMaxAgeSeconds": 31536000,
    "StrictTransportSecurityIncludeSubDomains": true,
    "StrictTransportSecurityPreload": false,
    "AddXFrameOptions": true,
    "XFrameOptionsValue": "DENY",
    "ContentSecurityPolicy": null,
    "PermissionsPolicy": null
  },
  "CorsOptions": {
    "Enabled": true,
    "AllowAllInDevelopment": false,
    "AllowedOrigins": ["https://my-frontend.example.com"],
    "AllowedMethods": ["GET", "POST", "PUT", "PATCH", "DELETE", "OPTIONS"],
    "AllowedHeaders": ["Content-Type", "Authorization", "X-Correlation-Id"],
    "AllowCredentials": true
  },
  "ApiVersionDeprecationOptions": {
    "Enabled": true,
    "PathPrefix": "/api",
    "DeprecatedVersions": [
      {
        "Version": "1.0",
        "DeprecationDate": "2025-07-01T00:00:00Z",
        "SunsetDate": "2026-07-01T00:00:00Z",
        "SunsetLink": "https://my-frontend.example.com/docs/api-v1-deprecation"
      }
    ]
  },
  "RequestLimitsOptions": {
    "Enabled": true,
    "MaxRequestBodySize": 10485760,
    "MaxRequestHeadersTotalSize": 32768,
    "MaxRequestHeaderCount": 100,
    "MaxFormValueCount": 1024
  },
  "ForwardedHeadersOptions": {
    "Enabled": true,
    "ForwardLimit": 2,
    "KnownProxies": [],
    "KnownNetworks": ["10.0.0.0/8"],
    "ClearDefaultProxies": true,
    "EnforceTrustedProxyConfigurationInProduction": true,
    "SuppressServerHeader": true
  }
}
```

In Kubernetes, these values are typically supplied via `appsettings.Production.json` plus environment-specific overrides from ConfigMaps/Secrets.

## Program.cs wiring

Each feature is registered and enabled individually, so consumer applications choose exactly which features to use.

```csharp
using ApiPipeline.NET.Extensions;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddApiPipeline(builder.Configuration);

builder.Services.AddAuthentication();
builder.Services.AddAuthorization();
builder.Services.AddControllers();

var app = builder.Build();

app.UseApiPipeline(pipeline => pipeline
    .WithForwardedHeaders()
    .WithCorrelationId()
    .WithExceptionHandler()
    .WithHttpsRedirection()
    .WithCors()
    .WithAuthentication()
    .WithAuthorization()
    .WithRateLimiting()
    .WithResponseCompression()
    .WithResponseCaching()
    .WithOutputCaching()
    .WithSecurityHeaders()
    .WithVersionDeprecation()
);

app.MapControllers();

app.Run();
```

### Selective registration

Disable specific service registrations you don't need:

```csharp
builder.Services.AddApiPipeline(builder.Configuration, options =>
{
    options.AddOutputCaching = true;     // opt-in to output caching
    options.AddResponseCaching = false;
    options.AddRequestSizeTracking = false;
});
```

### Pipeline order

`UseApiPipeline` enforces middleware in a fixed phase order regardless of the order you call `With*` methods. This prevents accidental misorderings:

1. `WithForwardedHeaders()` – resolve real client IP from proxy headers
2. `WithCorrelationId()` – assign/validate correlation ID
3. `WithExceptionHandler()` – catch unhandled exceptions as ProblemDetails
4. `WithHttpsRedirection()` – redirect HTTP to HTTPS (when TLS is not terminated at the ingress)
5. `WithCors()` – enforce CORS policies (before auth so preflight is not rate-limited)
6. `WithAuthentication()` / `WithAuthorization()` – authenticate and authorize requests
7. `WithRequestValidation()` – optional; only when you register `AddRequestValidation<T>()`
8. `WithRateLimiting()` – enforce rate limits (after identity for per-user partitioning)
9. `WithResponseCompression()` – compress eligible responses
10. `WithResponseCaching()` – serve cached responses (after auth to avoid caching unauthorized responses; auto `Vary: Origin` with CORS)
11. `WithOutputCaching()` – output caching with distributed store and tag-based invalidation (opt-in)
12. `WithSecurityHeaders()` – apply security headers (HSTS, X-Content-Type-Options, Referrer-Policy, X-Frame-Options)
13. `WithVersionDeprecation()` – add deprecation headers

Your own routing and endpoints are added **after** this pipeline.

### Skip methods

Use `Skip*` methods to exclude middleware you've already wired manually or don't need:

```csharp
app.UseApiPipeline(pipeline => pipeline
    .WithForwardedHeaders()
    .WithCorrelationId()
    .WithExceptionHandler()
    .SkipHttpsRedirection()    // TLS terminated at ingress
    .WithCors()
    .WithAuthentication()
    .WithAuthorization()
    .WithRateLimiting()
    .WithResponseCompression()
    .WithResponseCaching()
    .WithSecurityHeaders()
    .SkipVersionDeprecation()  // not using API versioning
);
```

Available skip methods: `SkipCorrelationId()`, `SkipExceptionHandler()`, `SkipHttpsRedirection()`, `SkipCors()`, `SkipAuthentication()`, `SkipAuthorization()`, `SkipRequestValidation()`, `SkipRateLimiting()`, `SkipResponseCompression()`, `SkipResponseCaching()`, `SkipOutputCaching()`, `SkipSecurityHeaders()`, `SkipVersionDeprecation()`, `SkipForwardedHeaders()`, `SkipRequestSizeTracking()`.

You can mirror these tests in consumer solutions using `Microsoft.AspNetCore.TestHost` if you want to assert that your app's Program.cs continues to wire the pipeline correctly.

## Security

- **Security headers**: `X-Content-Type-Options`, `Referrer-Policy`, HSTS (with optional `preload` directive), `X-Frame-Options`, `Content-Security-Policy`, and `Permissions-Policy` are all configurable via `SecurityHeadersSettings`. HSTS is automatically skipped in Development environments. CSP and Permissions-Policy default to `null` (omitted) — set them when your API serves browser-rendered content.
- **Correlation ID validation**: incoming `X-Correlation-Id` headers are validated against a strict source-generated regex (alphanumeric, hyphens, underscores, dots, max 128 chars). Invalid values are rejected to prevent header injection attacks.
- **Rate limiting**: partition keys prefer authenticated user identifiers (`sub`, `nameid`, `ClaimTypes.NameIdentifier`), then fall back to remote IP, then the `AnonymousFallback` behavior (`Reject`, `RateLimit`, or `Allow`). Default is `Reject` to prevent shared-bucket exhaustion. When `RateLimitingOptions.Enabled` is `true`, at least one policy must be configured and `DefaultPolicy` must reference a defined policy name. Use `WithForwardedHeaders()` when behind a proxy so rate limiting partitions on the real client IP.
- **CORS**: defaults to disabled. When enabled, `AllowAllInDevelopment: true` only takes effect in development environments. Production environments require explicit `AllowedOrigins`. `AllowedHeaders` defaults to `["Content-Type", "Authorization", "X-Correlation-Id"]` (not wildcard). When CORS is enabled outside development, `AllowedMethods`/`AllowedHeaders` must include at least one value.
- **Error response caching prevention**: all ProblemDetails error responses (including 429, 500, 404) include `Cache-Control: no-store` to prevent intermediate proxies and CDNs from caching error responses.
- **Do not rely solely on this package for security**: it complements, but does not replace, authentication/authorization and WAF protections. In Kubernetes, pair ApiPipeline.NET with:
  - Ingress-level protections (e.g. NGINX/Envoy limits and WAF rules).
  - Network policies and TLS termination at the ingress.
  - Your identity provider and ASP.NET Core authentication/authorization middleware.

## Production checklist

Before deploying to production, ensure:

| Area | Action |
| --- | --- |
| **HTTPS** | Terminate TLS at the ingress (or reverse proxy). Use `WithForwardedHeaders()` so the app sees the original scheme and client IP. |
| **CORS** | Set **explicit** `CorsOptions:AllowedOrigins` for production. Set `AllowAllInDevelopment: false` in production config. If `AllowCredentials` is `true`, you must configure at least one origin (wildcard is not allowed). |
| **Rate limiting** | Tune `RateLimitingOptions:Policies` (e.g. `PermitLimit`, `WindowSeconds`) for your expected load. Set `AnonymousFallback` to `Reject`. `DefaultPolicy` must match a configured policy name (validated at startup). Use named policies on sensitive endpoints. |
| **Forwarded headers** | Call `WithForwardedHeaders()` first in the pipeline so rate limiting and logging use the real client IP. In production, configure `KnownProxies`/`KnownNetworks` and set `ClearDefaultProxies: true`. `EnforceTrustedProxyConfigurationInProduction` is `true` by default. |
| **Exception handling** | `AddApiPipeline(...)` already registers exception handling services; keep `WithExceptionHandler()` in the middleware pipeline for structured RFC 7807 ProblemDetails responses with correlation IDs. |
| **Health checks** | Map health endpoints (e.g. `/health`, `/health/ready`, `/health/live`) and configure Kubernetes `readinessProbe` / `livenessProbe` to hit them. Keep these paths excluded from compression (default excludes `/health`). |
| **Request limits** | Set `RequestLimitsOptions` (body size, header count/size, form value count) to match your API contract and ingress limits. Use 10 MB as a baseline. |
| **Output caching** | Enable `OutputCachingOptions:Enabled` and register with `options.AddOutputCaching = true`. Use Redis-backed `IOutputCacheStore` in multi-instance deployments. Apply `.CacheOutput()` policies per endpoint with explicit `Expire` durations and tags. Use `EvictByTagAsync` in write endpoints to invalidate stale data. |
| **Security headers** | Enable `SecurityHeaders` with HSTS, `X-Content-Type-Options: nosniff`, `Referrer-Policy`, and `X-Frame-Options: DENY`. Add `ContentSecurityPolicy` and `PermissionsPolicy` when browser clients render your API domain. |

## Observability

- **Correlation IDs**:
  - Incoming requests may carry `X-Correlation-Id`; if not present, `CorrelationIdMiddleware` will generate one and echo it back on the response.
  - Access the resolved correlation ID in your code: `httpContext.Items["X-Correlation-Id"]?.ToString()`.
  - If you use Serilog or another structured logger, prefer enrichers that automatically attach the correlation ID to all events.
- **OpenTelemetry**:
  - The core package sets the **correlation ID** on the current `Activity` (tag `correlation_id`) and emits **metrics** via `System.Diagnostics.Metrics`.
  - To export these with OpenTelemetry, add the **ApiPipeline.NET.OpenTelemetry** package and call `builder.AddApiPipelineObservability()` (extension on `WebApplicationBuilder`).
  - Configuration is read from `AppSettings` and `OpenTelemetryOptions` sections in `appsettings.json`, with optional code-based overrides.
  - This automatically registers ApiPipeline.NET's `ActivitySource` and `Meter` with OpenTelemetry so all pipeline traces and metrics are exported. Metrics include:
    - `apipipeline.ratelimit.rejected` – rate limit rejections
    - `apipipeline.deprecation.headers_added` – deprecation headers emitted
    - `apipipeline.correlation_id.processed` – correlation IDs processed
    - `apipipeline.security_headers.applied` – responses with security headers
    - `apipipeline.exceptions.handled` – exceptions caught by the pipeline handler
  - In Kubernetes, export traces and metrics to a backend (e.g. Grafana Tempo, Jaeger, Azure Monitor, Prometheus) for cross-service correlation.
- **Health endpoints**:
  - `/health` is excluded from compression by default; map readiness and liveness endpoints for probes.
  - Configure Kubernetes `readinessProbe`/`livenessProbe` to point at these endpoints. See [OPERATIONS.md](docs/OPERATIONS.md) for a Kubernetes manifest example.

## Rate limiting in production

- **Partitioning**:
  - Global rate limiting is partitioned by user identity when available (`sub`, `nameid`, `ClaimTypes.NameIdentifier`), then API key (when `EnableApiKeyPartitioning` is `true`), falling back to remote IP, then `AnonymousFallback` behavior.
  - This is a good default for APIs where each authenticated user should have their own quota.
- **API key partitioning** (M2M traffic):
  - Set `EnableApiKeyPartitioning: true` and clients include `X-Api-Key: <key>` in requests.
  - Each API key gets its own rate limit bucket, supporting per-client SLAs without full auth infrastructure.
  - Customize the header name via `ApiKeyHeader` (default: `"X-Api-Key"`).
- **Informational headers**:
  - When `EmitRateLimitHeaders: true` (default), every response includes `X-RateLimit-Limit` and `X-RateLimit-Reset` headers.
  - Clients can use these to implement adaptive backoff (e.g., slow down when approaching the limit).
- **Anonymous fallback**:
  - `Reject` (default): returns 429 immediately when no identity or IP can be determined. Safe default.
  - `RateLimit`: uses a single shared anonymous bucket. Warning: one client can exhaust this for all anonymous traffic.
  - `Allow`: skips rate limiting entirely. Only use when you have upstream enforcement.
- **Token bucket** (recommended for burst-tolerant APIs):

```json
{
  "Name": "burst-tolerant",
  "Kind": "TokenBucket",
  "PermitLimit": 50,
  "WindowSeconds": 30,
  "TokensPerPeriod": 10,
  "QueueLimit": 5,
  "QueueProcessingOrder": "OldestFirst",
  "AutoReplenishment": true
}
```

- **Sliding window** (smoother distribution than fixed window):

```json
{
  "Name": "smooth",
  "Kind": "SlidingWindow",
  "PermitLimit": 100,
  "WindowSeconds": 60,
  "SegmentsPerWindow": 6,
  "QueueLimit": 0,
  "QueueProcessingOrder": "OldestFirst",
  "AutoReplenishment": true
}
```

- **Named policies**:
  - Define multiple policies in `RateLimitingOptions.Policies` and attach them to specific endpoints via `.RequireRateLimiting("policy-name")`.
  - This lets you keep a conservative global policy while allowing more permissive behavior on non-critical endpoints.
- **Rejection response format**:
  - Rate-limited responses use RFC 7807 ProblemDetails format (`application/problem+json`) with `status: 429`, a `Retry-After` header (when available from the limiter), and `Cache-Control: no-store` to prevent caching of error responses.
- **Fail-fast behavior**:
  - When `Enabled` is `true` but no policies are configured, ApiPipeline.NET fails validation on startup instead of silently running unprotected.
  - If `DefaultPolicy` doesn't match any configured policy name, startup fails with a clear validation error.
  - This behavior is desirable in containerized environments: pods fail fast with clear configuration errors instead of entering a bad-but-running state.

## Versioning and compatibility

- ApiPipeline.NET follows **Semantic Versioning** — see [docs/VERSIONING.md](docs/VERSIONING.md) for full policy.
  - **MAJOR**: breaking changes to public APIs or pipeline behavior.
  - **MINOR**: new features and configuration options that are backwards compatible.
  - **PATCH**: bug fixes and internal improvements only.
- The library currently targets **.NET 10** (`net10.0`) for the core package and tests.
- No external package is required for request limits; they are configured via `RequestLimitsOptions`.
- HTTP client resilience is intentionally separate: pair with `HttpResilience.NET`, `Microsoft.Extensions.Http.Resilience`, or custom policies.

## Pairing with HTTP client resilience

ApiPipeline.NET focuses on the **incoming** HTTP pipeline. For **outgoing** HTTP calls (`HttpClient`), you should still configure resilience through [HttpResilience.NET](https://github.com/baps-apps/http-resilience-net/pkgs/nuget/HttpResilience.NET) package.

ApiPipeline.NET remains agnostic here so you can adopt the resilience stack that best fits your platform.

## Extended docs

- [CHANGELOG.md](CHANGELOG.md) — version history
- [CONTRIBUTING.md](CONTRIBUTING.md) — development setup and PR guidelines
- [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) — pipeline design and middleware ordering
- [docs/INTERNALS.md](docs/INTERNALS.md) — implementation details and extension points
- [docs/OPERATIONS.md](docs/OPERATIONS.md) — production deployment and monitoring
- [docs/RUNBOOK.md](docs/RUNBOOK.md) — incident response procedures
- [docs/TROUBLESHOOTING.md](docs/TROUBLESHOOTING.md) — common issues and diagnostics
- [docs/VERSIONING.md](docs/VERSIONING.md) — versioning and compatibility policy
- [docs/migration.md](docs/migration.md) — upgrade and migration guide
- [docs/performance.md](docs/performance.md) — benchmark commands and load testing

### Feature deep-dives

- [Rate Limiting](docs/features/rate-limiting.md) — multi-policy, named policies, partition keys, anonymous fallback
- [Response Compression](docs/features/response-compression.md) — Brotli/Gzip, MIME types, path exclusions
- [Response Caching](docs/features/response-caching.md) — in-memory caching, Vary: Origin enforcement
- [Output Caching](docs/features/output-caching.md) — distributed caching, tag-based invalidation, migration from response caching
- [Security Headers](docs/features/security-headers.md) — HSTS, CSP, X-Frame-Options, Permissions-Policy
- [CORS](docs/features/cors.md) — development and production modes, credentials, validation
- [Correlation IDs](docs/features/correlation-id.md) — injection-safe validation, tracing, logging
- [API Version Deprecation](docs/features/version-deprecation.md) — Deprecation, Sunset, Link headers
- [Request Limits](docs/features/request-limits.md) — Kestrel + form body/header limits
- [Exception Handling](docs/features/exception-handling.md) — RFC 7807 ProblemDetails, anti-caching
- [Forwarded Headers](docs/features/forwarded-headers.md) — proxy trust, IP resolution, scheme detection
