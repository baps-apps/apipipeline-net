# ApiPipeline.NET Internals

This document explains internal design, implementation boundaries, and extension points for contributors and advanced consumers.

## Core assembly structure

```text
src/ApiPipeline.NET/
├── Configuration/
│   └── ApiPipelineConfigurationKeys.cs     # JSON section key constants
├── Cors/
│   └── LiveConfigCorsPolicyProvider.cs     # ICorsPolicyProvider (dev vs prod)
├── Extensions/
│   ├── ServiceCollectionExtensions.cs      # AddApiPipeline + per-feature Add* methods
│   └── WebApplicationExtensions.cs         # UseApiPipeline + per-feature Use* methods
├── Middleware/
│   ├── CorrelationIdMiddleware.cs
│   ├── ApiVersionDeprecationMiddleware.cs
│   ├── SecurityHeadersMiddleware.cs
│   ├── RequestValidationMiddleware.cs
│   └── RequestSizeMiddleware.cs            # Request body size histogram
├── Observability/
│   └── ApiPipelineTelemetry.cs             # ActivitySource, Meter, counter helpers
├── Options/
│   ├── ApiPipelineServiceRegistrationOptions.cs
│   ├── CorsSettings.cs
│   ├── ForwardedHeadersSettings.cs
│   ├── RateLimitingOptions.cs
│   ├── RequestLimitsOptions.cs
│   ├── OutputCachingSettings.cs
│   ├── ResponseCachingSettings.cs
│   ├── ResponseCompressionSettings.cs
│   ├── SecurityHeadersSettings.cs
│   ├── ApiVersionDeprecationOptions.cs
│   ├── ConfigureKestrelOptions.cs          # IConfigureOptions<KestrelServerOptions>
│   ├── ConfigureResponseCachingOptions.cs
│   └── ConfigureResponseCompressionOptions.cs
├── Pipeline/
│   ├── IApiPipelineBuilder.cs              # Fluent With*/Skip* contract
│   └── ApiPipelineBuilder.cs               # Phase-enforced middleware application
├── RateLimiting/
│   └── RateLimiterPolicyResolver.cs        # Resolves named/default policies via IOptionsMonitor
└── Validation/
    ├── IRequestValidationFilter.cs
    └── RequestValidationResult.cs
```

## Design principles

- **Configuration-driven**: behavior is defined in options, not hardcoded branching.
- **Fail fast where unsafe**: production misconfiguration (proxy trust, CORS, rate limiting) surfaces at startup.
- **Feature isolation**: each concern has independent options and registration. Features can be enabled/disabled independently.
- **Composability**: `AddApiPipeline` wraps per-feature extensions without preventing custom wiring.
- **Phase-enforced ordering**: `ApiPipelineBuilder` groups middleware into phases (Infrastructure → Security → Auth → Validation → RateLimiting → Output → Headers) and applies them in fixed order regardless of `With*` call order.
- **Vary: Origin enforcement**: when both CORS and response caching are enabled, `UseResponseCaching()` automatically injects a middleware that appends `Vary: Origin` via `OnStarting`, preventing cross-origin cache poisoning.
- **Output caching integration**: `OutputCachingSettings` provides an opt-in migration path from legacy `ResponseCachingMiddleware` to ASP.NET Core Output Caching, with distributed store and tag-based invalidation support.

## Request path internals

### Correlation ID

`CorrelationIdMiddleware` uses a source-generated regex (`[a-zA-Z0-9._-]{1,128}`) to validate incoming `X-Correlation-Id` headers. Invalid or missing values are replaced with `Activity.Current?.TraceId` or a new GUID. The resolved ID is stored in `HttpContext.Items["X-Correlation-Id"]` (using `CorrelationIdMiddleware.HeaderName`) and echoed on the response header.

```csharp
// Reading the correlation ID downstream:
var correlationId = httpContext.Items[CorrelationIdMiddleware.HeaderName]?.ToString();
```

### Exception handling

The exception handler converts unhandled failures into RFC 7807 `ProblemDetails` payloads. Every error response includes `Cache-Control: no-store` to prevent proxies from caching errors. The correlation ID and trace ID are included in the response for cross-referencing with logs.

### Security headers

`SecurityHeadersMiddleware` uses `OnStarting` callbacks with static tuple state (no closure allocation) to apply headers at response time. Headers applied:

| Header | Option | Default |
|---|---|---|
| `X-Content-Type-Options: nosniff` | `AddXContentTypeOptionsNoSniff` | `true` |
| `Referrer-Policy` | `ReferrerPolicy` | `"no-referrer"` |
| `Strict-Transport-Security` | `EnableStrictTransportSecurity` | `true` (skipped in Development) |
| HSTS `preload` directive | `StrictTransportSecurityPreload` | `false` |
| `X-Frame-Options` | `AddXFrameOptions` | `true` (`DENY`) |
| `Content-Security-Policy` | `ContentSecurityPolicy` | `null` (omitted) |
| `Permissions-Policy` | `PermissionsPolicy` | `null` (omitted) |

### Version deprecation

`ApiVersionDeprecationMiddleware` inspects versioned API routes starting with the configured `PathPrefix` (default `/api`) and conditionally adds `Deprecation`, `Sunset`, and `Link` headers. Requires an `IApiVersionReader` to be registered (provided by the `ApiPipeline.NET.Versioning` satellite package or a custom implementation).

### Rate limiting

`RateLimiterPolicyResolver` resolves named and default policies via `IOptionsMonitor<RateLimitingOptions>` (singleton, cache-invalidated on config change). Partition keys prefer authenticated user identity (`sub`, `nameid`, `ClaimTypes.NameIdentifier`), then API key (when `EnableApiKeyPartitioning` is `true`), then fall back to remote IP, then `AnonymousFallback` behavior (`Reject`, `RateLimit`, or `Allow`).

The resolved `RateLimitPolicy` object is stored in `HttpContext.Items["ApiPipeline.RateLimitPolicyConfig"]` during the global limiter and named policy callbacks. When `EmitRateLimitHeaders` is `true` (default), a middleware registered before `UseRateLimiter()` uses `OnStarting` to add `X-RateLimit-Limit` and `X-RateLimit-Reset` headers to every response.

### Request size tracking

`RequestSizeMiddleware` records request body sizes as a histogram metric via `ApiPipelineTelemetry`. Intended to run immediately after forwarded headers for accurate client IP context.

## Options and validation behavior

- Options are bound from configuration sections defined in `ApiPipelineConfigurationKeys`.
- All options use `ValidateDataAnnotations().ValidateOnStart()` for fail-fast startup validation.
- Validators guard incompatible or dangerous combinations:
  - CORS: `AllowCredentials` + wildcard origin is rejected.
  - CORS: `AllowedMethods`/`AllowedHeaders` must have values when CORS is enabled outside dev.
  - Rate limiting: `DefaultPolicy` must reference a defined policy name. At least one policy required when enabled.
  - Forwarded headers: production environments fail if no proxy trust is configured (controllable via `EnforceTrustedProxyConfigurationInProduction`).
- Request limits are projected onto both `KestrelServerOptions` and `FormOptions` so behavior is consistent for JSON and multipart payloads.

## Telemetry internals

`ApiPipelineTelemetry` provides static `ActivitySource` and `Meter` instances shared across all middleware:

| Metric | Type | When emitted |
|---|---|---|
| `apipipeline.ratelimit.rejected` | Counter | Rate limit rejection (429) |
| `apipipeline.deprecation.headers_added` | Counter | Deprecation headers emitted |
| `apipipeline.correlation_id.processed` | Counter | Correlation ID processed |
| `apipipeline.security_headers.applied` | Counter | Security headers applied |
| `apipipeline.exceptions.handled` | Counter | Exceptions caught by handler |

The `ApiPipeline.NET.OpenTelemetry` satellite package registers these with OpenTelemetry providers via `builder.AddApiPipelineObservability()`.

## Extension points

### Custom request validation

```csharp
public class MyValidationFilter : IRequestValidationFilter
{
    public Task<RequestValidationResult> ValidateAsync(HttpContext context)
    {
        if (context.Request.ContentLength > 1_000_000)
            return Task.FromResult(RequestValidationResult.Failure("Request too large"));

        return Task.FromResult(RequestValidationResult.Success());
    }
}

// Registration:
builder.Services.AddApiPipeline(builder.Configuration)
    .AddRequestValidation<MyValidationFilter>();

// Pipeline:
app.UseApiPipeline(pipeline => pipeline
    // ...
    .WithRequestValidation()
    // ...
);
```

### Custom API version reader

Implement `IApiVersionReader` if you use a versioning scheme other than Asp.Versioning:

```csharp
public class HeaderApiVersionReader : IApiVersionReader
{
    public string? GetApiVersion(HttpContext context)
        => context.Request.Headers["X-Api-Version"].FirstOrDefault();
}
```

### Selective registration

```csharp
builder.Services.AddApiPipeline(builder.Configuration, options =>
{
    options.AddResponseCaching = false;      // don't register caching
    options.AddRequestSizeTracking = false;  // skip body size histogram
    options.AddForwardedHeaders = false;     // handle forwarded headers externally
});
```

### Per-feature wiring

For partial adoption, wire features individually instead of `AddApiPipeline`:

```csharp
builder.Services
    .AddCorrelationId()
    .AddRateLimiting(builder.Configuration)
    .AddSecurityHeaders(builder.Configuration)
    .AddCors(builder.Configuration);
```

## Testing approach

The test suite verifies:

- Option validation and registration behavior (`OptionsValidationTests`)
- Middleware outputs and header behavior (`CorrelationIdMiddlewareTests`, `SecurityHeadersMiddlewareTests`, etc.)
- Phase-enforced pipeline ordering (`PipelineBuilderTests`, `PipelineOrderingTests`)
- Smoke-level end-to-end pipeline behavior through `TestServer` patterns (`ApiPipelineSmokeTests`)
- CORS policy resolution across environments (`CorsTests`)
- Forwarded headers trust and XFF processing (`ForwardedHeadersTests`)
- Rate limiting algorithms and 429 responses (`RateLimitingTests`)

When adding new middleware behavior, add tests that assert both enabled and disabled feature paths.
