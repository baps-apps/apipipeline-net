## ApiPipeline.NET

Shared ASP.NET Core API pipeline that centralizes:

- **Rate limiting**
- **Response compression**
- **Response caching**
- **Security headers** (HSTS, X-Content-Type-Options, Referrer-Policy)
- **CORS**
- **Correlation IDs** (with input validation against header injection)
- **API version deprecation headers**
- **Kestrel and form request limits** (via `RequestLimitsOptions`)
- **Structured exception handling** (RFC 7807 ProblemDetails with correlation ID)
- **Forwarded headers** (for correct client IP behind proxies/ingress)

Consumer solutions reference **ApiPipeline.NET NuGet package** (from a feed or local `.nupkg`), not a project reference.

## Table of contents

- [Benefits](#benefits)
- [What it configures](#what-it-configures)
- [Installation](#installation)
- [Configuration](#configuration)
- [Program.cs wiring](#programcs-wiring)
- [Kestrel request limits](#kestrel-request-limits)
- [Exception handling](#exception-handling)
- [Forwarded headers](#forwarded-headers)
- [Tests](#tests)
- [Security](#security)
- [Production checklist](#production-checklist)
- [Observability](#observability)
- [Versioning and compatibility](#versioning-and-compatibility)

## Benefits

This package is primarily a **standardization + maintenance win** for ASP.NET Core APIs:

- **One pipeline, many apps**: consistent rate limiting, compression, caching, CORS, security headers, and deprecation headers across all services.
- **Fewer bugs and regressions**: behavior changes are implemented **once** in ApiPipeline.NET instead of being hand-rolled in each repo.
- **Faster onboarding**: new APIs get a hardened pipeline by:
  - Adding the package
  - Adding configuration sections
  - Calling the per-feature `Add`*/`Use`* extensions and `ConfigureKestrelRequestLimits`.
- **Safer operations**: many behaviors are feature-flagged in config (e.g. disable rate limiting or security headers) without code changes.
- **Shared Kestrel limits**: request/body/header limits come from `RequestLimitsOptions` so they are consistent across services.

## What it configures

ApiPipeline.NET provides:

- **Rate limiting**
  - **Options**: `RateLimitingOptions` – a collection of named policies built on `System.Threading.RateLimiting` primitives (fixed window, sliding window, concurrency, token bucket), aligned with Polly’s rate limiter strategy.
  - **Registration**: `AddRateLimiting(builder.Configuration)` wires ASP.NET Core `AddRateLimiter` using those options.
  - **Middleware**: `UseRateLimiting()` applies rate limiting when `RateLimitingOptions.Enabled` is `true`, returning RFC 7807 ProblemDetails (`application/problem+json`) with `Retry-After` header and `Cache-Control: no-store` when clients exceed limits.
- **Response compression**
  - **Options**: `ResponseCompressionSettings` – enable/disable, Brotli/Gzip providers, MIME types to compress, and paths to exclude.
  - **Registration**: `AddResponseCompression(builder.Configuration)` configures ASP.NET Core `AddResponseCompression` based on those settings.
  - **Middleware**: `UseResponseCompression()` compresses eligible responses, skipping any paths listed in `ExcludedPaths` (e.g. `/health`).
- **Response caching**
  - **Options**: `ResponseCachingSettings` – global cache size limit and whether paths are case‑sensitive.
  - **Registration**: `AddResponseCaching(builder.Configuration)` registers `AddResponseCaching` with the configured limits.
  - **Middleware**: `UseResponseCaching()` enables ASP.NET Core response caching so cacheable responses can be served without recomputing them.
- **Security headers**
  - **Options**: `SecurityHeadersSettings` – HSTS (`Strict-Transport-Security`), `Referrer-Policy`, and `X-Content-Type-Options: nosniff`. Focused on API-relevant headers only (browser-only headers like CSP, X-Frame-Options, and Permissions-Policy are intentionally excluded).
  - **Registration**: `AddSecurityHeaders(builder.Configuration)` binds those settings.
  - **Middleware**: `SecurityHeadersMiddleware` + `UseSecurityHeaders()` apply all configured security headers via `OnStarting` callbacks so they are set even when downstream components write directly to the response. HSTS is automatically skipped in Development environments.
- **CORS**
  - **Options**: `CorsSettings` – enable/disable, dev‑only allow‑all flag, allowed origins/methods/headers, and whether credentials are allowed.
  - **Registration**: `AddCors(builder.Configuration)` registers CORS policies from configuration.
  - **Middleware**: `UseCors()` uses an allow‑all policy in development (when configured) or a restrictive policy based on `AllowedOrigins`, `AllowedMethods`, and `AllowedHeaders` in other environments.
- **Correlation ID**
  - **Extensions**: `.AddCorrelationId()` registers the middleware; `.UseCorrelationId()` adds it to the ASP.NET Core pipeline.
  - **Behavior**: `CorrelationIdMiddleware` reads an incoming `X-Correlation-Id` header, validates it against a strict pattern (alphanumeric, hyphens, underscores, dots, max 128 chars) to prevent header injection, and echoes it back. Invalid or missing IDs are replaced with a server-generated value (from the current `Activity.TraceId` or a new GUID). The ID is stored in `HttpContext.Items` for downstream use.
- **API version deprecation**
  - **Options**: `ApiVersionDeprecationOptions` – list of deprecated API versions with optional deprecation/sunset dates and documentation links. The `PathPrefix` property (default `/api`) controls which routes are inspected.
  - **Registration**: `AddApiVersionDeprecation(builder.Configuration)` binds those options.
  - **Middleware**: `ApiVersionDeprecationMiddleware` + `UseApiVersionDeprecation()` add `Deprecation`, `Sunset`, and `Link` headers for matching API versions on routes starting with the configured `PathPrefix`.
- **Kestrel and form request limits**
  - **Options**: `RequestLimitsOptions` – maximum body size, total header size, header count, and maximum form value count.
  - **Registration**: `AddRequestLimits(builder.Configuration)` binds limits and configures ASP.NET Core `FormOptions` to enforce the same body/count limits for form and multipart requests.
  - **Kestrel**: `ConfigureKestrelRequestLimits()` (on `WebApplicationBuilder`) maps the same options into `KestrelServerLimits` so all HTTP traffic respects the configured ceilings.

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

ApiPipeline.NET expects strongly-typed options bound from configuration sections. Typical **development** `appsettings.json`:

```json
{
  "RateLimitingOptions": {
    "Enabled": true,
    "DefaultPolicy": "strict",
    "Policies": [
      {
        "Name": "strict",
        "Kind": "FixedWindow",
        "PermitLimit": 4,
        "WindowSeconds": 12,
        "QueueLimit": 2,
        "QueueProcessingOrder": "OldestFirst",
        "AutoReplenishment": true
      },
      {
        "Name": "permissive",
        "Kind": "FixedWindow",
        "PermitLimit": 200,
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
    "ExcludedPaths": [ "/health" ]
  },
  "ResponseCachingOptions": {
    "Enabled": true,
    "SizeLimitBytes": 104857600,
    "UseCaseSensitivePaths": false
  },
  "SecurityHeaders": {
    "Enabled": true,
    "ReferrerPolicy": "no-referrer",
    "AddXContentTypeOptionsNoSniff": true,
    "EnableStrictTransportSecurity": true,
    "StrictTransportSecurityMaxAgeSeconds": 31536000,
    "StrictTransportSecurityIncludeSubDomains": true
  },
  "CorsOptions": {
    "Enabled": true,
    "AllowAllInDevelopment": true,
    "AllowedOrigins": [ "https://myapp.com" ],
    "AllowedMethods": [ "GET", "POST", "PUT", "PATCH", "DELETE", "OPTIONS" ],
    "AllowedHeaders": [ "*" ],
    "AllowCredentials": true
  },
  "ApiVersionDeprecationOptions": {
    "Enabled": true,
    "DeprecatedVersions": [
      {
        "Version": "1.0",
        "DeprecationDate": "Tue, 01 Jul 2025 00:00:00 GMT",
        "SunsetDate": "Thu, 01 Jan 2026 00:00:00 GMT",
        "SunsetLink": "https://myapp.com/docs/api-v1-deprecation"
      }
    ]
  },
  "RequestLimitsOptions": {
    "Enabled": true,
    "MaxRequestBodySize": 1048576,
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
    "SuppressServerHeader": true
  }
}
```

Configuration keys are defined in `ApiPipeline.NET.Configuration.ApiPipelineConfigurationKeys` for reuse.

For **production** (for example, an API running behind a Kubernetes ingress), you will usually want slightly different defaults:

```json
{
  "RateLimitingOptions": {
    "Enabled": true,
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
    "ExcludedPaths": [
      "/health"
    ]
  },
  "ResponseCachingOptions": {
    "Enabled": true,
    "SizeLimitBytes": 104857600,
    "UseCaseSensitivePaths": false
  },
  "SecurityHeaders": {
    "Enabled": true,
    "ReferrerPolicy": "no-referrer",
    "AddXContentTypeOptionsNoSniff": true,
    "EnableStrictTransportSecurity": true,
    "StrictTransportSecurityMaxAgeSeconds": 31536000,
    "StrictTransportSecurityIncludeSubDomains": true
  },
  "CorsOptions": {
    "Enabled": true,
    "AllowAllInDevelopment": true,
    "AllowedOrigins": [
      "https://my-frontend.example.com"
    ],
    "AllowedMethods": [
      "GET",
      "POST",
      "PUT",
      "PATCH",
      "DELETE",
      "OPTIONS"
    ],
    "AllowedHeaders": [
      "*"
    ],
    "AllowCredentials": true
  },
  "ApiVersionDeprecationOptions": {
    "Enabled": true,
    "DeprecatedVersions": [
      {
        "Version": "1.0",
        "DeprecationDate": "Tue, 01 Jul 2025 00:00:00 GMT",
        "SunsetDate": "Thu, 01 Jan 2026 00:00:00 GMT",
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
    .AddCorrelationId()
    .AddRateLimiting(builder.Configuration)
    .AddResponseCompression(builder.Configuration)
    .AddResponseCaching(builder.Configuration)
    .AddSecurityHeaders(builder.Configuration)
    .AddCors(builder.Configuration)
    .AddApiVersionDeprecation(builder.Configuration)
    .AddRequestLimits(builder.Configuration)
    .AddForwardedHeaders(builder.Configuration)
    .AddApiPipelineExceptionHandler();

builder.ConfigureKestrelRequestLimits();

builder.Services.AddAuthentication();
builder.Services.AddAuthorization();
builder.Services.AddControllers();

var app = builder.Build();

app.UseApiPipelineForwardedHeaders();
app.UseCorrelationId();
app.UseApiPipelineExceptionHandler();
app.UseHttpsRedirection();
app.UseRateLimiting();
app.UseResponseCompression();
app.UseResponseCaching();
app.UseSecurityHeaders();
app.UseApiVersionDeprecation();
app.UseCors();

app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

app.Run();
```

### Pipeline order

The recommended pipeline order is:

1. `UseApiPipelineForwardedHeaders` – resolve real client IP from proxy headers
2. `UseCorrelationId` – assign/validate correlation ID
3. `UseApiPipelineExceptionHandler` – catch unhandled exceptions as ProblemDetails
4. `UseHttpsRedirection` – redirect HTTP to HTTPS (when TLS is not terminated at the ingress)
5. `UseRateLimiting` – enforce rate limits
6. `UseResponseCompression` – compress eligible responses
7. `UseResponseCaching` – serve cached responses
8. `UseSecurityHeaders` – apply security headers (HSTS, X-Content-Type-Options, Referrer-Policy)
9. `UseApiVersionDeprecation` – add deprecation headers
10. `UseCors` – enforce CORS policies
11. `UseAuthentication` / `UseAuthorization` – authenticate and authorize requests

Your own routing and endpoints are added **after** this pipeline.

In production, the same pattern applies; you typically also add OpenTelemetry and logging enrichment (see [Observability](#observability)).

## Kestrel request limits

Kestrel limits are driven by `RequestLimitsOptions`:

- `MaxRequestBodySize`
- `MaxRequestHeadersTotalSize`
- `MaxRequestHeaderCount`

ApiPipeline.NET provides an extension on `WebApplicationBuilder`:

```csharp
using ApiPipeline.NET.Extensions;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddJsonFile("appsettings.json", optional: false);

// Configure all Kestrel limits from RequestLimitsOptions
builder.ConfigureKestrelRequestLimits();
```

Form request limits (`FormOptions`) are also configured from `RequestLimitsOptions`, so multipart and form posts respect the same body/count limits as Kestrel.

## Exception handling

ApiPipeline.NET provides structured RFC 7807 ProblemDetails error responses with correlation ID and trace ID enrichment.

### Registration

```csharp
builder.Services.AddApiPipelineExceptionHandler();
```

### Middleware

```csharp
app.UseCorrelationId();
app.UseApiPipelineExceptionHandler();
```

Place `UseApiPipelineExceptionHandler` early in the pipeline (after `UseCorrelationId`) so all downstream exceptions are caught.

### Behavior

When an unhandled exception occurs, the response is a `application/problem+json` body:

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.6.1",
  "title": "An error occurred while processing your request.",
  "status": 500,
  "correlationId": "abc-123",
  "traceId": "00-abcdef1234567890-abcdef12-01"
}
```

- `correlationId` is taken from `X-Correlation-Id` (propagated or generated).
- `traceId` is the OpenTelemetry trace ID or ASP.NET Core trace identifier.
- `Cache-Control: no-store` is set on all ProblemDetails responses to prevent proxy/CDN caching of error responses.
- `UseStatusCodePages` is also enabled, so bare 404/405/etc. status codes produce ProblemDetails responses.

## Forwarded headers

When running behind a reverse proxy, load balancer, or Kubernetes ingress, client IP addresses are not directly available. Use `UseApiPipelineForwardedHeaders` to apply `X-Forwarded-For`, `X-Forwarded-Proto`, and `X-Forwarded-Host` from trusted proxies:

```csharp
builder.Services.AddForwardedHeaders(builder.Configuration);
// ...
app.UseApiPipelineForwardedHeaders();
```

This must be the **first** middleware in the pipeline so that `RemoteIpAddress` reflects the real client IP before rate limiting partitions on it.

Configuration is read from `ForwardedHeadersOptions` in appsettings:

```json
{
  "ForwardedHeadersOptions": {
    "Enabled": true,
    "ForwardLimit": 2,
    "KnownProxies": [],
    "KnownNetworks": ["10.0.0.0/8"],
    "ClearDefaultProxies": true,
    "SuppressServerHeader": true
  }
}
```

| Property | Default | Description |
|----------|---------|-------------|
| `Enabled` | `true` | Master switch for forwarded headers processing. |
| `ForwardLimit` | `1` | Max proxy hops to process from `X-Forwarded-For`. Set to the number of trusted proxies. |
| `KnownProxies` | `[]` | Explicit proxy IP addresses to trust (e.g. `["10.0.0.1"]`). |
| `KnownNetworks` | `[]` | CIDR ranges to trust (e.g. `["10.0.0.0/8"]`). Useful in K8s where proxy IPs are dynamic. |
| `ClearDefaultProxies` | `false` | Clear default loopback trust before applying custom lists. Required in most cloud deployments. |
| `SuppressServerHeader` | `true` | Suppresses the `Server: Kestrel` response header to prevent server fingerprinting. |

> **Security note**: Leaving `KnownProxies` and `KnownNetworks` empty defaults to trusting only loopback. Behind a real load balancer, `X-Forwarded-For` will be silently ignored unless you configure trusted proxies or set `ClearDefaultProxies: true`.

## Tests

The package ships with `ApiPipeline.NET.Tests` (xUnit + FluentAssertions + TestHost), comprising 46 tests across 8 test classes:

| Test class | Coverage |
|---|---|
| `ApiPipelineSmokeTests` | Full pipeline integration, rate limit rejection |
| `CorrelationIdMiddlewareTests` | Valid propagation, XSS/CRLF injection rejection, length limits |
| `SecurityHeadersMiddlewareTests` | API-relevant headers (HSTS, X-Content-Type-Options, Referrer-Policy), HSTS per-environment, browser-only header exclusion |
| `RateLimitingTests` | FixedWindow, SlidingWindow, Concurrency, TokenBucket policies, disabled mode, 429 ProblemDetails format |
| `OptionsValidationTests` | Disabled skip, missing policies, DefaultPolicy mismatch, duplicate policy names, FixedWindow/SlidingWindow/TokenBucket field requirements, ForwardedHeaders disabled |
| `ExceptionHandlerTests` | ProblemDetails format, correlation ID in errors, 404 handling |
| `CorsTests` | Disabled, AllowAll in dev, explicit origins, preflight OPTIONS |
| `ForwardedHeadersTests` | X-Forwarded-For applied, safe without headers, disabled skips, ForwardLimit respected, ClearDefaultProxies |

Run tests with:

```bash
dotnet test tests/ApiPipeline.NET.Tests
```

You can mirror these tests in consumer solutions using `Microsoft.AspNetCore.TestHost` if you want to assert that your app’s Program.cs continues to wire the pipeline correctly.

## Security

- **Security headers**: `X-Content-Type-Options`, `Referrer-Policy`, and HSTS are configurable via `SecurityHeadersSettings`. These are the headers relevant to API-only services. Browser-only headers (CSP, X-Frame-Options, Permissions-Policy) are intentionally excluded since APIs return JSON, not HTML. HSTS is automatically skipped in Development environments.
- **Correlation ID validation**: incoming `X-Correlation-Id` headers are validated against a strict pattern (alphanumeric, hyphens, underscores, dots, max 128 chars). Invalid values are rejected to prevent header injection attacks.
- **Rate limiting**: partition keys prefer authenticated user identifiers (`sub`, `nameid`, `ClaimTypes.NameIdentifier`), then fall back to remote IP, then `anonymous`. When `RateLimitingOptions.Enabled` is `true`, at least one policy must be configured and `DefaultPolicy` must reference a defined policy name. Use `UseApiPipelineForwardedHeaders` when behind a proxy so rate limiting partitions on the real client IP.
- **CORS**: defaults to allow-all only in development when `AllowAllInDevelopment` is `true`; production environments should configure explicit `AllowedOrigins`. In non-development environments, an empty `AllowedOrigins` set will deny all browser origins by design.
- **Error response caching prevention**: All ProblemDetails error responses (including 429, 500, 404) include `Cache-Control: no-store` to prevent intermediate proxies and CDNs from caching error responses.
- **Do not rely solely on this package for security**: it complements, but does not replace, authentication/authorization and WAF protections. In Kubernetes, you should pair ApiPipeline.NET with:
  - Ingress-level protections (e.g. NGINX/Envoy limits and WAF rules).
  - Network policies and TLS termination at the ingress.
  - Your identity provider and ASP.NET Core authentication/authorization middleware.

## Production checklist

Before deploying to production, ensure:

| Area | Action |
|------|--------|
| **HTTPS** | Terminate TLS at the ingress (or reverse proxy). Use `UseApiPipelineForwardedHeaders()` so the app sees the original scheme and client IP. |
| **CORS** | Set **explicit** `CorsOptions:AllowedOrigins` for production. Do not use allow-all outside development. If `AllowCredentials` is `true`, you must configure at least one origin (wildcard is not allowed). |
| **Rate limiting** | Tune `RateLimitingOptions:Policies` (e.g. `PermitLimit`, `WindowSeconds`) for your expected load. `DefaultPolicy` must match a configured policy name (validated at startup). Use named policies on sensitive endpoints. |
| **Forwarded headers** | Call `UseApiPipelineForwardedHeaders()` first in the pipeline so rate limiting and logging use the real client IP (not the proxy IP). |
| **Exception handling** | Call `AddApiPipelineExceptionHandler()` and `UseApiPipelineExceptionHandler()` for structured RFC 7807 ProblemDetails error responses with correlation IDs. |
| **Health checks** | Map health endpoints (e.g. `/health`, `/health/readiness`, `/health/liveness`) and configure Kubernetes `readinessProbe` / `livenessProbe` to hit them. Keep these paths excluded from compression (default excludes `/health`). |
| **Request limits** | Set `RequestLimitsOptions` (body size, header count/size, form value count) to match your API contract and ingress limits. |
| **Security headers** | Enable `SecurityHeaders` with HSTS, `X-Content-Type-Options: nosniff`, and `Referrer-Policy`. These are the API-relevant security headers; browser-only headers (CSP, X-Frame-Options, Permissions-Policy) are not included since APIs serve JSON, not HTML. |

## Observability

- **Correlation IDs**:
  - Incoming requests may carry `X-Correlation-Id`; if not present, `CorrelationIdMiddleware` will generate one and echo it back on the response.
  - You can enrich logs with the correlation ID by adding a logging scope, for example:
    ```csharp
    app.Use(async (context, next) =>
    {
        var logger = context.RequestServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger("RequestLogging");

        if (context.Items.TryGetValue("X-Correlation-Id", out var correlationId) && correlationId is string cid)
        {
            using (logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = cid }))
            {
                await next(context);
            }
        }
        else
        {
            await next(context);
        }
    });
    ```
  - If you use Serilog or another structured logger, prefer enrichers that automatically attach the correlation ID to all events.
- **OpenTelemetry**:
  - The core package sets the **correlation ID** on the current `Activity` (tag `correlation_id`) and emits **metrics** via `System.Diagnostics.Metrics` (rate limit rejections, deprecation headers). To export these with OpenTelemetry, add the **ApiPipeline.NET.OpenTelemetry** package and call `AddApiPipelineObservability()` on the `WebApplicationBuilder`:
    ```csharp
    using ApiPipeline.NET.OpenTelemetry;

    builder.AddApiPipelineObservability();
    ```
  - Configuration is read from `AppSettings` and `OpenTelemetryOptions` sections in `appsettings.json`. You can also pass an options callback:
    ```csharp
    builder.AddApiPipelineObservability(options =>
    {
        options.EnableTracing = true;
        options.EnableMetrics = true;
        options.EnableLogging = true;
    });
    ```
  - This automatically registers ApiPipeline.NET's `ActivitySource` and `Meter` with OpenTelemetry so all pipeline traces and metrics are exported. Metrics include:
    - `apipipeline.ratelimit.rejected` – rate limit rejections
    - `apipipeline.deprecation.headers_added` – deprecation headers emitted
    - `apipipeline.correlation_id.processed` – correlation IDs processed
    - `apipipeline.security_headers.applied` – responses with security headers
    - `apipipeline.exceptions.handled` – exceptions caught by the pipeline handler
  - In Kubernetes, export traces and metrics to a backend (e.g. Grafana Tempo, Jaeger, Azure Monitor, Prometheus) for cross-service correlation.
- **Health endpoints**:
  - `/health` is excluded from compression by default; you can map readiness/liveness endpoints (for Kubernetes probes) and keep them fast and small:
    ```csharp
    app.MapHealthChecks("/health/readiness");
    app.MapHealthChecks("/health/liveness");
    ```
  - Configure Kubernetes `readinessProbe`/`livenessProbe` to point at these endpoints.

## Rate limiting in production

- **Partitioning**:
  - Global rate limiting is partitioned by user identity when available (`sub`, `nameid`, `ClaimTypes.NameIdentifier`), falling back to remote IP, then `anonymous`.
  - This is a good default for APIs where each authenticated user should have their own quota.
- **Token bucket** (recommended for burst-tolerant APIs):
  - Set `Kind` to `TokenBucket`, configure `PermitLimit` (max bucket size), `WindowSeconds` (replenishment period), and `TokensPerPeriod` (tokens added each period). This allows controlled bursts while enforcing a sustained rate.
    ```json
    {
      "Name": "burst-tolerant",
      "Kind": "TokenBucket",
      "PermitLimit": 100,
      "WindowSeconds": 10,
      "TokensPerPeriod": 20,
      "QueueLimit": 0,
      "QueueProcessingOrder": "OldestFirst",
      "AutoReplenishment": true
    }
    ```
- **Named policies**:
  - You can define multiple policies in `RateLimitingOptions.Policies` and attach them to specific endpoints:
    ```csharp
    app.MapGet("/orders", handler).RequireRateLimiting("per-user-burst");
    ```
  - This lets you keep a conservative global policy while allowing more permissive behavior on non-critical endpoints.
- **Rejection response format**:
  - Rate-limited responses use RFC 7807 ProblemDetails format (`application/problem+json`) with `status: 429`, a `Retry-After` header (when available from the limiter), and `Cache-Control: no-store` to prevent caching of error responses.
- **Fail-fast behavior**:
  - When `Enabled` is `true` but no policies are configured, ApiPipeline.NET fails validation on startup instead of silently running unprotected.
  - If `DefaultPolicy` doesn't match any configured policy name, startup fails with a clear validation error.
  - This behavior is desirable in containerized environments: pods fail fast with clear configuration errors instead of entering a bad-but-running state.

## Versioning and compatibility

- ApiPipeline.NET follows **Semantic Versioning**:
  - **MAJOR**: breaking changes to public APIs or pipeline behavior.
  - **MINOR**: new features and configuration options that are backwards compatible.
  - **PATCH**: bug fixes and internal improvements only.
- The library currently targets **.NET 10** (`net10.0`) for the core package and tests.
- No external package is required for request limits; they are configured via `RequestLimitsOptions`.
- HTTP client resilience is intentionally kept separate: you can pair ApiPipeline.NET with your preferred resilience library (for example, `Microsoft.Extensions.Http.Resilience` or a custom library). A future version of ApiPipeline.NET may expose a dedicated `AddHttpResilience(...)` extension that binds resilience options from configuration and wires them into `IHttpClientFactory`, but there is no hard dependency today.

## Pairing with HTTP client resilience

ApiPipeline.NET focuses on the **incoming** HTTP pipeline. For **outgoing** HTTP calls (`HttpClient`), you should still configure resilience through [HttpResilience.NET](https://github.com/baps-apps/http-resilience-net/pkgs/nuget/HttpResilience.NET) package. 

ApiPipeline.NET remains agnostic here so you can adopt the resilience stack that best fits your platform.