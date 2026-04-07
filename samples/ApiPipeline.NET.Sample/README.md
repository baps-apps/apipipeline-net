### ApiPipeline.NET Sample

This sample shows how to consume `ApiPipeline.NET` from a typical ASP.NET Core application and how to configure each option via configuration files.

The sample provides:

- `appsettings.json`: shared defaults.
- `appsettings.Development.json`: development-friendly overrides (HSTS disabled, allow-all CORS, permissive rate limiting).
- `appsettings.Production.json`: production-oriented defaults for a typical Kubernetes-hosted API:
  - Strict CORS (`AllowedOrigins` listing only trusted frontends).
  - API-relevant security headers (HSTS, X-Content-Type-Options, Referrer-Policy).
  - Rate limiting tuned to your expected traffic and SLAs.

Override specific values per environment via environment variables or config maps/secrets.

Run the sample from the repository root:

```bash
dotnet run --project samples/ApiPipeline.NET.Sample
```

### Configuration scenarios (options pattern)

| Scenario | When to use | Where it lives |
|----------|-------------|----------------|
| **Shared defaults** | Baseline JSON merged into every environment | `appsettings.json` |
| **Development** | Open CORS, permissive rate limits, HSTS off | `appsettings.Development.json` (`ASPNETCORE_ENVIRONMENT=Development`) |
| **Production / K8s ingress** | Strict CORS, trusted proxy CIDRs, `AnonymousFallback: Reject` | `appsettings.Production.json` |
| **Copy-paste fragments** | Minimal API, ingress, null-IP rate limit, output-cache migration | `ConfigurationSnippets/*.json` + `ConfigurationSnippets/README.md` |

Override any key via [environment variables](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/configuration/#environment-variables) or secrets (for example `RateLimitingOptions__Enabled=false`).

### Configuration section names (JSON ↔ `IConfiguration`)

These names match `ApiPipeline.NET.Configuration.ApiPipelineConfigurationKeys` and the options classes they bind to:

| JSON section | Options type |
|--------------|----------------|
| `RateLimitingOptions` | `RateLimitingOptions` |
| `ResponseCompressionOptions` | `ResponseCompressionSettings` |
| `ResponseCachingOptions` | `ResponseCachingSettings` |
| `SecurityHeaders` | `SecurityHeadersSettings` |
| `CorsOptions` | `CorsSettings` |
| `ApiVersionDeprecationOptions` | `ApiVersionDeprecationOptions` |
| `RequestLimitsOptions` | `RequestLimitsOptions` |
| `ForwardedHeadersOptions` | `ForwardedHeadersSettings` |

Then browse to:

- `https://localhost:5001/api/v1/orders` (deprecated version, will include deprecation headers)
- `https://localhost:5001/api/v2/orders` (current version)

---

### Wiring the pipeline and versioned API

- **In `Program.cs`** (see repository file for the full, commented version):

```csharp
using ApiPipeline.NET.Extensions;
using ApiPipeline.NET.OpenTelemetry;
using ApiPipeline.NET.Sample;
using ApiPipeline.NET.Validation;
using ApiPipeline.NET.Versioning;
using Asp.Versioning;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddApiPipeline(builder.Configuration)
    .AddApiPipelineVersioning(builder.Configuration)
    .AddRequestValidation<SampleRequestValidationFilter>();

builder.AddApiPipelineObservability();

builder.Services.AddAuthentication();
builder.Services.AddAuthorization();
builder.Services.AddControllers();
builder.Services.AddApiVersioning(options =>
{
    options.DefaultApiVersion = new ApiVersion(1, 0);
    options.AssumeDefaultVersionWhenUnspecified = true;
    options.ReportApiVersions = true;
    options.ApiVersionReader = new UrlSegmentApiVersionReader();
});

var app = builder.Build();

app.UseApiPipeline(pipeline => pipeline
    .WithForwardedHeaders()
    .WithCorrelationId()
    .WithExceptionHandler()
    .WithHttpsRedirection()
    .WithCors()
    .WithAuthentication()
    .WithAuthorization()
    .WithRequestValidation()
    .WithRateLimiting()
    .WithResponseCompression()
    .WithResponseCaching()
    .WithSecurityHeaders()
    .WithVersionDeprecation());

app.MapGet("/health", () => Results.Ok(new { Status = "Healthy" }));

app.MapControllers();

app.Run();
```

The `ApiVersionDeprecationMiddleware` will attach `Deprecation`, `Sunset`, and `Link` headers to responses from `v1` endpoints under `/api` that match the configured deprecated version.

All behavior is driven by the configuration sections below.

---

### RateLimitingOptions (`RateLimitingOptions`)

**Purpose**: Protects your API from abuse and noisy neighbors by limiting how many requests a client can make per time window.

**Configuration (in `appsettings.json`)**:

```json
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
}
```

- **Enabled**: When `false`, no rate limiting middleware is applied.
- **Policies**: A list of named policies built on `System.Threading.RateLimiting` primitives (fixed window, sliding window, concurrency, token bucket).
- **DefaultPolicy**: Controls which policy is used for the **global** limiter.
- **AnonymousFallback**: When `RemoteIpAddress` is null (misconfigured forwarded headers, some test hosts), choose `Reject` (429), `RateLimit` (shared bucket), or `Allow`. Production ingress configs usually use `Reject`; local development often uses `RateLimit` so TestHost still exercises rate limits.

**How it is used**:

- `UseRateLimiting` wires `UseRateLimiter()` when `Enabled` is `true`.
- You can decorate endpoints with:

```csharp
app.MapGet("/weather", handler)
   .RequireRateLimiting("strict");
```

**Why it matters**: Centralized rate limiting prevents a single client (by user ID or IP) from overwhelming your API and degrading quality of service for others.

---

### ResponseCompressionOptions (`ResponseCompressionOptions`)

**Purpose**: Reduces payload size to save bandwidth and improve perceived performance.

**Configuration**:

```json
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
}
```

- **Enabled**: When `false`, compression is not applied.
- **EnableForHttps**: Enables compression over HTTPS.
- **EnableBrotli / EnableGzip**: Choose compression providers.
- **MimeTypes**: Which content types are compressible.
- **ExcludedMimeTypes**: Mime types that must never be compressed.
- **ExcludedPaths**: Paths (e.g. `/health`) that should not be compressed.

**How it is used**:

- `AddResponseCompression` configures ASP.NET Core response compression and an `IConfigureOptions<ResponseCompressionOptions>` that reads these settings.
- `UseResponseCompression` applies compression except on `ExcludedPaths`.

**Why it matters**: Proper compression keeps latency and bandwidth under control, particularly for large JSON responses, while still allowing opt‑outs for small or sensitive endpoints like health checks.

---

### ResponseCachingOptions (`ResponseCachingOptions`)

**Purpose**: Lets the server short‑circuit work and serve cached responses when appropriate.

**Configuration**:

```json
"ResponseCachingOptions": {
  "Enabled": true,
  "SizeLimitBytes": 52428800,
  "UseCaseSensitivePaths": false
}
```

- **Enabled**: When `false`, `UseResponseCaching` is not added.
- **SizeLimitBytes**: Upper bound on the in‑memory cache size.
- **UseCaseSensitivePaths**: Whether `/Weather` and `/weather` are treated as different cache keys.
- **PreferOutputCaching**: When `true`, signals intent to migrate to Output Caching via `WithOutputCaching()` in the pipeline. This flag does not change behavior on its own.

**How it is used**:

- `AddResponseCaching` registers response caching and configures `ResponseCachingOptions`.
- `UseResponseCaching` adds `UseResponseCaching()` when `Enabled` is `true`.
- Endpoints opt in to caching via standard ASP.NET Core mechanisms (e.g. cache headers or output caching).

**Why it matters**: Caching can drastically reduce load and latency for expensive or frequently requested endpoints when used alongside correct cache headers.

---

### SecurityHeaders (`SecurityHeaders`)

**Purpose**: Applies security HTTP headers (HSTS, Referrer‑Policy, X‑Content‑Type‑Options) and optional browser-facing headers (`Content-Security-Policy`, `X-Frame-Options`, `Permissions-Policy`) in a central place.

**Configuration**:

```json
"SecurityHeaders": {
  "Enabled": true,
  "ReferrerPolicy": "no-referrer",
  "AddXContentTypeOptionsNoSniff": true,
  "EnableStrictTransportSecurity": true,
  "StrictTransportSecurityMaxAgeSeconds": 31536000,
  "StrictTransportSecurityIncludeSubDomains": true
}
```

- **Enabled**: When `false`, middleware is still in the pipeline but becomes a no‑op.
- **ReferrerPolicy**: `Referrer-Policy` header value.
- **AddXContentTypeOptionsNoSniff**: Controls `X-Content-Type-Options: nosniff`. Prevents browsers from MIME-sniffing JSON responses as executable content.
- **EnableStrictTransportSecurity**: When `true`, adds `Strict-Transport-Security` (HSTS) header. Automatically skipped in Development environments.
- **StrictTransportSecurityMaxAgeSeconds**: HSTS `max-age` in seconds (default: 31536000 = 1 year).
- **StrictTransportSecurityIncludeSubDomains**: Whether to append `includeSubDomains` to HSTS.

**How it is used**:

- `SecurityHeadersMiddleware` applies all configured security headers via `OnStarting` callbacks so they are set even when downstream components write directly to the response.
- `UseSecurityHeaders()` adds the middleware to the pipeline.

**Why it matters**: HSTS enforces HTTPS for all future requests, `X-Content-Type-Options: nosniff` prevents MIME-type sniffing attacks on JSON responses, and `Referrer-Policy` controls what referrer information is sent with requests.

---

### CorsOptions (`CorsOptions`)

**Purpose**: Centralizes CORS rules for browser callers.

**Configuration**:

```json
"CorsOptions": {
  "Enabled": true,
  "AllowAllInDevelopment": true,
  "AllowedOrigins": [],
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
  "AllowCredentials": false
}
```

- **Enabled**: When `false`, no CORS middleware is applied.
- **AllowAllInDevelopment**: When `true` in `Development`, uses the permissive `AllowAll` policy.
- **AllowedOrigins**: Explicit list of allowed origins for non‑development environments.
- **AllowedMethods**: Allowed HTTP methods (or `*` to allow any).
- **AllowedHeaders**: Allowed request headers (or `*` to allow any).
- **AllowCredentials**: Whether cookies/authorization headers can be sent cross‑origin.

**How it is used**:

- `AddCors` registers CORS policies.
- `UseCors`:
  - In development, uses `AllowAll` when `AllowAllInDevelopment` is `true`.
  - Otherwise builds a restrictive policy based on configured origins/methods/headers.

**Why it matters**: Correct CORS configuration is critical for SPAs and mobile apps while avoiding over‑permissive cross‑origin access in production.

---

### ApiVersionDeprecationOptions (`ApiVersionDeprecationOptions`)

**Purpose**: Signals deprecated API versions to clients using standard `Deprecation`, `Sunset`, and `Link` headers.

**Configuration**:

```json
"ApiVersionDeprecationOptions": {
  "Enabled": true,
  "DeprecatedVersions": [
    {
      "Version": "1.0",
      "DeprecationDate": "2025-01-01T00:00:00Z",
      "SunsetDate": "2026-01-01T00:00:00Z",
      "SunsetLink": "https://example.com/api/v1-sunset"
    }
  ]
}
```

- **Enabled**: Turns the feature on or off.
- **PathPrefix**: The URL path prefix that triggers deprecation header inspection (default: `/api`).
- **DeprecatedVersions**: Array of version entries that describe when a version is deprecated/sunset and where to read more.

**How it is used**:

- `ApiVersionDeprecationMiddleware` runs after the endpoint:
  - Only for paths under the configured `PathPrefix` (default `/api`).
  - Reads the requested API version (via ASP.NET API Versioning).
  - If the version is listed as deprecated, adds:
    - `Deprecation` header (`true` or an RFC‑1123 date).
    - Optional `Sunset` header.
    - Optional `Link` header with `rel="sunset"`.

**Why it matters**: Makes version deprecation visible and machine‑readable to clients, encouraging upgrades before breaking changes.

---

### RequestLimitsOptions (`RequestLimitsOptions`)

**Purpose**: Central control over Kestrel and form upload limits to prevent abusive payloads and header floods.

**Configuration**:

```json
"RequestLimitsOptions": {
  "Enabled": true,
  "MaxRequestBodySize": 104857600,
  "MaxRequestHeadersTotalSize": 16384,
  "MaxRequestHeaderCount": 100,
  "MaxFormValueCount": 1024
}
```

- **Enabled**: When `false`, no Kestrel/form limits are applied.
- **MaxRequestBodySize**: Applies to Kestrel `MaxRequestBodySize` and form multipart/body buffer limits.
- **MaxRequestHeadersTotalSize**: Total allowed size of all request headers.
- **MaxRequestHeaderCount**: Maximum number of headers.
- **MaxFormValueCount**: Maximum number of form values (prevents overly large form posts).

**How it is used**:

- `AddRequestLimits` binds `RequestLimitsOptions`, applies them to Kestrel via `IConfigureOptions<KestrelServerOptions>`, and configures ASP.NET Core `FormOptions` from the same settings (body size and value count limits).

**Why it matters**: Defends your APIs from very large uploads or header attacks that can exhaust memory or CPU, while letting you raise limits for known safe scenarios (e.g. file upload APIs).

---

### Request size tracking (`AddRequestSizeTracking` / `UseRequestSizeTracking`)

**Purpose**: Records incoming `Content-Length` values to the `apipipeline.request.body_bytes` metric for capacity planning.

**Registration**:

- `AddApiPipeline(builder.Configuration)` already registers request-size tracking.
- Call `app.UseRequestSizeTracking()` **immediately after** `UseApiPipelineForwardedHeaders()` for accurate source IP context.

---

### Request validation (`AddRequestValidation<T>` / `WithRequestValidation`)

**Purpose**: OWASP API7-style hook — run `IRequestValidationFilter` implementations before your endpoints execute.

**Registration**:

- Implement `IRequestValidationFilter` (see `SampleRequestValidationFilter.cs` in this project).
- `AddRequestValidation<TFilter>()` registers your filter; `UseApiPipeline(... WithRequestValidation())` adds the middleware after authentication/authorization.

---

### Output Caching (migration from Response Caching)

**Purpose**: Output Caching is the modern replacement for `ResponseCachingMiddleware`, supporting distributed stores (Redis), tag-based eviction, and per-endpoint policies.

**When migrating**:

1. Enable output caching: set `"OutputCachingOptions": { "Enabled": true }` in `appsettings.json`.
2. Register it: call `AddApiPipeline(configuration, options => { options.AddOutputCaching = true; })` or `AddOutputCaching(configuration)`.
3. Add `WithOutputCaching()` to the pipeline (after auth, alongside or instead of `WithResponseCaching()`).
4. Attach per-endpoint policies via `.CacheOutput()`.
5. Set `"PreferOutputCaching": true` in `ResponseCachingOptions` so operators know the intent.

---

### HTTP client resilience (outgoing calls)

ApiPipeline.NET does not change how you configure **outgoing** HTTP calls. For production APIs, you should still use `IHttpClientFactory` and a resilience layer:

- With `Microsoft.Extensions.Http.Resilience`:

  ```csharp
  builder.Services.AddHttpClient("downstream")
      .AddStandardResilienceHandler();
  ```

- Or with Polly:

  ```csharp
  builder.Services.AddHttpClient("downstream")
      .AddPolicyHandler(retryPolicy)
      .AddPolicyHandler(timeoutPolicy)
      .AddPolicyHandler(circuitBreakerPolicy);
  ```

Configuration for these policies is application-specific and typically comes from your own options bound from `appsettings.json`.

