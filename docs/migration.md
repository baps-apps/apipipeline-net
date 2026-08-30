# Migration Guide: 1.1.0 → 2.0.0

Mostly mechanical code edits plus configuration moves. Work through the sections in order.

## Quick checklist

- [ ] Move configuration under `ApiPipeline` ([3a](#3a-every-section-moved-under-apipipeline)),
      keeping the old sections until the rollout is done ([7](#7-rolling-back-to-110))
- [ ] Add `.RequireSecurityFeaturesEnabled()` to `UseApiPipeline`, so a section left unmoved fails
      startup instead of running without its protection ([3a](#3a-every-section-moved-under-apipipeline))
- [ ] Remove `using ApiPipeline.NET.Extensions;`, rename `Add*` calls ([2a](#2a-namespaces-and-method-names))
- [ ] `using ApiPipeline.NET.Options;` → `using ApiPipeline.NET;`, rename option types ([2b](#2b-option-types-renamed))
- [ ] Remove `RequireCors`/`[EnableCors]` naming `"AllowAll"` or `"Configured"` ([2b](#2b-option-types-renamed))
- [ ] Delete `Skip*()` calls ([2c](#2c-removed-builder-methods))
- [ ] Add your own `AddProblemDetails()` ([2d](#2d-exception-handling-is-now-yours))
- [ ] Drop `AddApiPipeline`'s options callback ([2e](#2e-addapipipeline-has-no-options-callback))
- [ ] Remove `ApiPipeline.NET.OpenTelemetry`, call `AddMeter` ([2f](#2f-opentelemetry-package-removed))
- [ ] Rename and convert durations and moved keys ([3b](#3b-renamed-moved-or-removed-keys))
- [ ] API key partitioning: add `KnownApiKeyHashes` ([3c](#3c-new-startup-checks))
- [ ] Deprecation headers: switch to `ApiPipeline.NET.Versioning` ([2g](#2g-api-version-deprecation-moved-to-its-own-package))
- [ ] Update clients reading `X-RateLimit-*` ([4a](#4a-rate-limiting))
- [ ] Update dashboards ([5](#5-telemetry-changes))
- [ ] Order `With*()` calls as recommended ([4d](#4d-pipeline-order))
- [ ] Cacheable endpoints set their own `Cache-Control` ([4b](#4b-security-headers))

## 1. Packages

| 1.1.0 | 2.0.0 |
| --- | --- |
| `ApiPipeline.NET` | `ApiPipeline.NET` 2.0.0 |
| `ApiPipeline.NET.Versioning` (`Asp.Versioning.Mvc`) | 2.0.0, on `Asp.Versioning.Http` (MVC and minimal APIs) |
| — | `ApiPipeline.NET.RateLimiting.Redis` 2.0.0 (optional), on StackExchange.Redis 3.4.35 or later |
| `ApiPipeline.NET.OpenTelemetry` | Removed ([2f](#2f-opentelemetry-package-removed)) |

## 2. Code changes (compile errors)

### 2a. Namespaces and method names

Delete `using ApiPipeline.NET.Extensions;`: extensions are now in Microsoft's namespaces. Registration
methods gained an `ApiPipeline` prefix (`AddCors(config)` → `AddApiPipelineCors(config)`, and so on
for every feature). `AddApiPipeline(configuration)` registers all of them. Removed:
`AddApiPipelineExceptionHandler()` ([2d](#2d-exception-handling-is-now-yours)) and
`AddRequestValidation<T>()` ([2c](#2c-removed-builder-methods)).

### 2b. Option types renamed

Each `*Settings`/`*Options` type is now `ApiPipeline<Feature>Options` in namespace `ApiPipeline.NET`
(for example `CorsSettings` → `ApiPipelineCorsOptions`, `RateLimitingOptions` →
`ApiPipelineRateLimitingOptions`, `RateLimitPolicy` → `ApiPipelineRateLimitPolicy`). `RateLimiterKind`
and `AnonymousFallbackBehavior` moved there too. `ApiVersionDeprecationOptions` is
`ApiPipeline.NET.Versioning.ApiPipelineVersionDeprecationOptions`, and its entry type
`ApiPipeline.NET.Options.DeprecatedVersion` is `ApiPipeline.NET.Versioning.DeprecatedVersion`, both in
the `ApiPipeline.NET.Versioning` package. `ApiPipelineServiceRegistrationOptions` is removed.

`CorsPolicyNames` is removed, and so are the named policies `"AllowAll"` and `"Configured"`: the
configured policy is now the default policy, applied to every endpoint by `WithCors()`. Remove
`RequireCors(...)` and `[EnableCors(...)]` that name either policy. A string literal such as
`[EnableCors("Configured")]` still compiles, but finds no policy, so that endpoint sends no CORS
headers and browsers block it. To exclude an endpoint from CORS, use `[DisableCors]`.

### 2c. Removed builder methods

`UseApiPipeline` extends `IApplicationBuilder` and takes `Action<ApiPipelineBuilder>`; existing
lambdas compile.

| Removed | Instead |
| --- | --- |
| `Skip*()` | Don't call the `With*()` |
| `WithExceptionHandler()` | Your own handler ([2d](#2d-exception-handling-is-now-yours)) |
| `WithRequestValidation()`, `IRequestValidationFilter` | `IEndpointFilter`, `[ApiController]` or `AddValidation()` |
| `WithVersionDeprecation()` | Nothing ([2g](#2g-api-version-deprecation-moved-to-its-own-package)) |
| Single-feature `app.Use*()` | `UseApiPipeline(p => p.With...())` |

### 2d. Exception handling is now yours

Register `AddProblemDetails` and put `UseExceptionHandler()` and `UseStatusCodePages()` inside the
pipeline after `WithCorrelationId()`, as in the README's [error handling](../README.md#error-handling).
The package still writes its own 429 and timeout bodies, through your `IProblemDetailsService`.

### 2e. `AddApiPipeline` has no options callback

`AddApiPipeline(configuration, options => ...)` no longer compiles. Every feature is registered and
stays off until its `Enabled` is `true`; to keep one off, leave `Enabled` false or skip its `With*()`.
`ApiPipeline:Cors:Enabled: false` also returns CORS to your own `AddCors` policies.

### 2f. OpenTelemetry package removed

Replace `builder.AddApiPipelineTelemetrySources()` with:

```csharp
builder.Services.ConfigureOpenTelemetryMeterProvider(m => m.AddMeter(ApiPipelineTelemetry.MeterName));
```

There is no `ActivitySource`. `ApiPipelineTelemetry` keeps only `MeterName` and `CorrelationIdTagName`.

### 2g. API version deprecation moved to its own package

`Asp.Versioning` writes the headers; `ApiPipeline.NET.Versioning` supplies the dates.

```diff
- builder.Services.AddApiVersionDeprecation(builder.Configuration);
+ builder.Services.AddApiVersioning(o =>
+ {
+     o.ReportApiVersions = true;                               // required for the headers
+     o.ApiVersionReader = new UrlSegmentApiVersionReader();    // the reader your API uses
+ });
+ builder.Services.AddApiPipelineVersionDeprecation(builder.Configuration);
- app.UseApiPipeline(p => p.WithVersionDeprecation() /* ... */);
```

`IApiVersionReader` is removed (use an `Asp.Versioning` reader) and so is `PathPrefix`. Set
`ApiVersionReader` explicitly: left unset, `Asp.Versioning` raises warning AV0015, which fails a
build that treats warnings as errors. See
[version-deprecation.md](features/version-deprecation.md).

### 2h. Other API changes

- `context.Items["X-Correlation-Id"]` is gone: use `HttpContext.GetCorrelationId()` and `GetTraceId()`.
- Middleware types are internal; `ApiPipelineBuilder` has no public constructor.
- Option lists are `IList<T>` (use `.Count`, not `.Length`); `DefaultExcludedPaths` is read-only.
- `WindowSeconds` and `StrictTransportSecurityMaxAgeSeconds` are `TimeSpan` `Window` and
  `StrictTransportSecurityMaxAge`.
- Binding one feature to two different sections throws.

## 3. Configuration changes

### 3a. Every section moved under `ApiPipeline`

Old top-level sections are not read: features run on defaults until moved, and startup warns about
an old section whose `ApiPipeline:*` counterpart is missing. Rate limiting, request limits and
request timeouts are off by default, so a section left unmoved turns its protection off. Add
`.RequireSecurityFeaturesEnabled()` to `UseApiPipeline` so that, outside Development, a security
phase whose feature is off fails startup instead:

```csharp
app.UseApiPipeline(pipeline => pipeline
    .RequireSecurityFeaturesEnabled()
    .WithForwardedHeaders()
    // ...
    .WithRateLimiting());
```

It covers `WithForwardedHeaders()`, `WithSecurityHeaders()`, `WithRequestTimeouts()`,
`WithEdgeRateLimiting()` and `WithRateLimiting()`. Request limits have no phase; check that
`ApiPipeline:RequestLimits:Enabled` is set. Keep the old sections during the
rollout so the previous build can be redeployed ([7](#7-rolling-back-to-110)). Each
`<Feature>Options` section becomes `ApiPipeline:<Feature>`, for example `CorsOptions` →
`ApiPipeline:Cors`, `ApiVersionDeprecationOptions` → `ApiPipeline:VersionDeprecation`.

```diff
 {
-  "CorsOptions": { "Enabled": true },
+  "ApiPipeline": {
+    "Cors": { "Enabled": true }
+  }
 }
```

Environment variables likewise: `RateLimitingOptions__Enabled` → `ApiPipeline__RateLimiting__Enabled`.

### 3b. Renamed, moved or removed keys

| Old key | Now |
| --- | --- |
| `Policies:N:WindowSeconds: 60` | `Policies:N:Window: "00:01:00"` (old key fails startup) |
| `StrictTransportSecurityMaxAgeSeconds: 31536000` | `StrictTransportSecurityMaxAge: "365.00:00:00"` (old key fails startup) |
| `ForwardedHeadersOptions:EnforceTrustedProxyConfigurationInProduction` | `ApiPipeline:ForwardedHeaders:EnforceTrustedProxyConfiguration`, applied in every environment but Development |
| `ForwardedHeadersOptions:SuppressServerHeader` | `ApiPipeline:RequestLimits:SuppressServerHeader` |
| `PreferOutputCaching`, `PathPrefix`, `AutoReplenishment` | Removed; delete them |

### 3c. New startup checks

| Configuration | Fix |
| --- | --- |
| `EnableApiKeyPartitioning` without `KnownApiKeyHashes` | Add [the hashes](features/rate-limiting.md#api-key-partitioning) |
| Invalid policy (`PermitLimit: 0`, missing `TokensPerPeriod`, `SegmentsPerWindow` above 1000) | Fix the policy |
| Response and output caching both enabled | Keep output caching |
| Deprecated version without `DeprecationDate`, `SunsetLink` without `SunsetDate`, sunset before deprecation | Fix the dates |
| CORS credentials with `"*"`, or an origin that can't match | Use the value the error gives ([origin format](features/cors.md#origin-format)) |
| Outside Development: your own default CORS policy plus `ApiPipeline:Cors:Enabled` | Move it to `ApiPipeline:Cors`, or name it |
| Outside Development: `ProcessHost` without real `AllowedHosts` | Set `AllowedHosts` |
| Outside Development: a `KnownNetworks` entry containing every client address (`0.0.0.0/0`, `::/0`) | List only your proxies' ranges |

### 3d. CORS lists replace the defaults

A configured `AllowedMethods` or `AllowedHeaders` now replaces the default list instead of adding to it.

## 4. Behavior changes

### 4a. Rate limiting

- `X-RateLimit-*` is replaced by IETF `RateLimit-Policy` and `RateLimit`, off by default
  (`EmitRateLimitHeaders`, `EmitRateLimitHeadersOnRejection`). `Retry-After` is always sent.
- The 429 body goes through your `IProblemDetailsService`; add headers to a 429 in
  `Response.OnStarting`.
- A named policy is no longer also capped by the default policy.
- IPv6 clients are grouped by `/64`; only allow-listed API keys get their own bucket.

### 4b. Security headers

On by default, and early in the pipeline, so 401/403/429/504 get them. A header your app sets is
never overwritten. New defaults to check:

- **`Cache-Control: no-store`**: endpoints a CDN or browser should cache must set their own.
- **`Content-Security-Policy: default-src 'none'; frame-ancestors 'none'`**: HTML pages such as
  Swagger UI need [their own policy](features/security-headers.md#content-security-policy-and-html-pages).
- **`Cross-Origin-Resource-Policy: same-origin`**: other sites can't embed responses.
- **HSTS** is the framework's: HTTPS only, not to loopback.

### 4c. Correlation IDs

- New `X-Trace-Id` header (`EmitTraceIdHeader: false` turns it off).
- Generated IDs are random GUIDs, no longer the trace ID.
- Trace tag `correlation_id` → `apipipeline.correlation_id`.

### 4d. Pipeline order

- Phases run in call order; 1.1.0 re-sorted them. Startup logs each broken rule; a broken security
  rule fails startup outside Development. See the [recommended order](../README.md#pipeline-order).
- A `With*()` phase without its registration fails the build.
- Routing is added by the pipeline.

### 4e. Configuration is read once

Settings are no longer reloaded live; changes apply on restart
([details](../README.md#configuration-is-read-once)). An invalid edit fails the next start instead
of returning 500s.

### 4f. Version deprecation headers

`Deprecation` uses RFC 9745 (`@1751328000`); `Asp.Versioning` writes `Link` and the
`api-*-versions` headers; `SunsetLink` sends only `rel="sunset"` (add `DeprecationLink` for
`rel="deprecation"`); short-circuited responses get no deprecation headers.

## 5. Telemetry changes

| 1.1.0 | 2.0.0 |
| --- | --- |
| `apipipeline.request.body_bytes` | `apipipeline.request.body.size` (bytes) |
| `apipipeline.ratelimit.rejected` (`policy_name`, `partition_type`) | `apipipeline.rate_limiting.rejected` (`apipipeline.rate_limiting.policy`, `apipipeline.rate_limiting.partition_type`) |
| `apipipeline.correlation_id.processed`, `apipipeline.exceptions.handled`, `apipipeline.deprecation.headers_added` | Removed |
| Activity source `ApiPipeline.NET` | Removed |
| — | New: `apipipeline.edge_rate_limiting.rejected`, `apipipeline.correlation_id.rejected` |

Old series stop at the upgrade; update dashboards and alerts.

## 6. New, optional features

Edge rate limiting, request timeouts, slow-client limits, correlation options, HSTS host exclusions,
`ProcessHost`, and `Action<TOptions>` overloads. See the [feature guides](../README.md#features-and-defaults).

## 7. Rolling back to 1.1.0

The code changes in section 2 don't compile against 1.1.0, so a rollback is a redeploy of the
previous build (image, lock files and all), not a package downgrade.

### Keep both configuration shapes until the rollout is done

1.1.0 reads only the top-level `<Feature>Options` sections; 2.0.0 reads only `ApiPipeline:*` and
ignores a legacy section, without the startup warning, when its `ApiPipeline:*` counterpart exists.
Add the `ApiPipeline` sections next to the old ones, rather than in place of them, and both builds
read the same configuration file, so rolling back changes no configuration:

```json
{
  "RateLimitingOptions": {
    "Enabled": true,
    "DefaultPolicy": "default",
    "Policies": [ { "Name": "default", "Kind": "FixedWindow", "PermitLimit": 100, "WindowSeconds": 60 } ]
  },
  "ApiPipeline": {
    "RateLimiting": {
      "Enabled": true,
      "DefaultPolicy": "default",
      "Policies": [ { "Name": "default", "Kind": "FixedWindow", "PermitLimit": 100, "Window": "00:01:00" } ]
    }
  }
}
```

Do the same for environment variables (`RateLimitingOptions__…` and `ApiPipeline__RateLimiting__…`)
and for secrets. Delete the old sections once no 1.1.0 build can be redeployed. If they are already
gone, restore them from the configuration the 1.1.0 build ran with before rolling back: without them
1.1.0 runs every feature on its defaults.

### What changes on rollback

| Area | Effect |
| --- | --- |
| Redis rate limiting | Limits become per pod; while both versions run, 1.1.0 pods don't count against the shared buckets. Keys need no cleanup: each expires after its window, refill time or `ConcurrencyLeaseTimeout`. To remove them at once, `SCAN` for `<KeyPrefix>:apipipeline:rl:*` and `UNLINK` the matches. |
| Response headers | `X-RateLimit-Limit` and `X-RateLimit-Reset` return; `RateLimit` and `RateLimit-Policy` stop. `Deprecation` returns to an HTTP date (or `true`) instead of `@<seconds>`. `Retry-After` is sent by both. Clients updated for 2.0.0 must accept both until the rollout is done. |
| Telemetry | The 1.1.0 series in [section 5](#5-telemetry-changes) return and the 2.0.0 ones stop. Keep panels and alerts for both until the rollout is done. |
| Startup checks | The checks in [3c](#3c-new-startup-checks) don't run under 1.1.0. Settings they would reject, such as an empty proxy trust set, start silently. |
| 2.0.0-only features | Edge rate limiting, request timeouts, slow-client limits and the other features in section 6 stop; 1.1.0's own exception handler returns. |

Rolling forward again needs no extra step: Redis buckets still within their expiry resume where
they left off.
