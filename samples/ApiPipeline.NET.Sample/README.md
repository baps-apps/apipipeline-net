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

Then browse to:

- `https://localhost:5001/api/v1/orders` (deprecated version, will include deprecation headers)
- `https://localhost:5001/api/v2/orders` (current version)

---

### Wiring the pipeline and versioned API

- **In `Program.cs`**:

```csharp
using ApiPipeline.NET.Extensions;
using ApiPipeline.NET.OpenTelemetry;
using Asp.Versioning;

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

builder.AddApiPipelineObservability();
builder.ConfigureKestrelRequestLimits();

builder.Services.AddControllers();
builder.Services.AddApiVersioning(options =>
{
    options.DefaultApiVersion = new ApiVersion(1, 0);
    options.AssumeDefaultVersionWhenUnspecified = true;
    options.ReportApiVersions = true;
    options.ApiVersionReader = new UrlSegmentApiVersionReader();
});

var app = builder.Build();

app.UseApiPipelineForwardedHeaders();
app.UseCorrelationId();
app.UseApiPipelineExceptionHandler();
app.UseHttpsRedirection();
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiting();
app.UseResponseCompression();
app.UseResponseCaching();
app.UseSecurityHeaders();
app.UseApiVersionDeprecation();

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

**How it is used**:

- `AddResponseCaching` registers response caching and configures `ResponseCachingOptions`.
- `UseResponseCaching` adds `UseResponseCaching()` when `Enabled` is `true`.
- Endpoints opt in to caching via standard ASP.NET Core mechanisms (e.g. cache headers or output caching).

**Why it matters**: Caching can drastically reduce load and latency for expensive or frequently requested endpoints when used alongside correct cache headers.

---

### SecurityHeaders (`SecurityHeaders`)

**Purpose**: Applies API-relevant security HTTP headers (HSTS, Referrer‑Policy, X‑Content‑Type‑Options) in a central place. Browser-only headers (CSP, X‑Frame‑Options, Permissions‑Policy) are intentionally excluded since APIs return JSON, not HTML.

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

- `ConfigureKestrelRequestLimits` binds `RequestLimitsOptions` and sets:
  - `KestrelServerLimits.MaxRequestBodySize`
  - `MaxRequestHeadersTotalSize`
  - `MaxRequestHeaderCount`
- `AddRequestLimits` also automatically configures ASP.NET Core `FormOptions` based on the same settings (body size and value count limits).

**Why it matters**: Defends your APIs from very large uploads or header attacks that can exhaust memory or CPU, while letting you raise limits for known safe scenarios (e.g. file upload APIs).

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

