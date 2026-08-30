# Changelog

All notable changes to **ApiPipeline.NET**, **ApiPipeline.NET.Versioning** and
**ApiPipeline.NET.RateLimiting.Redis**. Format: [Keep a Changelog](https://keepachangelog.com/en/1.1.0/);
versioning: [SemVer](https://semver.org/).

## [2.0.0] - 2026-11-15

Breaking; relative to 1.1.0. Upgrade with [docs/migration.md](docs/migration.md). Development notes:
[docs/archive/changelog-2.0.0-development-notes.md](docs/archive/changelog-2.0.0-development-notes.md).

### Added

- **`ApiPipeline.NET.RateLimiting.Redis`**: rate limit policies shared across replicas through Redis
  5.0+, all four kinds, with per-policy fallback (`PerPod`, `Allow`, `Reject`), a circuit breaker,
  HMAC-hashed keys, a warning for a plain-text connection to another host, and metrics and events
  100–107
  ([guide](docs/features/rate-limiting.md#shared-limits-across-pods)).
- **Rate limiting**: `GlobalPolicy`; `PartitionClaims` and `IRateLimitPartitionKeyResolver`;
  `ExcludedPaths` and `ProbeNetworks` for health probes (`GET`/`HEAD` only); `MaxIpPartitions` and
  `MaxIdentityPartitions`; the `IApiPipelineRateLimiterFactory` seam.
- **Edge rate limiting** before authentication (`WithEdgeRateLimiting()`).
- **Request timeouts** (`WithRequestTimeouts()`), with an RFC 9457 body.
- **Request limits**: Kestrel slow-client limits; IIS in-process and HTTP.sys support, warning for
  limits they can't enforce (event 70, category `ApiPipeline.NET.RequestLimitsServerWarning`);
  skipped on source-built runtimes, whose IIS and HTTP.sys assemblies are stubs.
- **CORS**: `PreflightMaxAge`; rate-limit headers exposed to browsers.
- **Correlation**: options, `X-Trace-Id`, and `AddApiPipelineCorrelationIdPropagation()`.
- **Forwarded headers**: `ProcessHost` bounded by `AllowedHosts`. **HSTS**: host exclusions.
- **Version deprecation**: `DeprecationLink`.
- **Pipeline**: `WithMiddleware(...)`, `WithRouting()`, `Action<TOptions>` overloads, any
  configuration section, and the composed order logged. Opt-in `RequireSecurityFeaturesEnabled()`
  fails startup outside Development when a security phase's feature is disabled.
- **Startup warnings**: phase without its feature enabled (15) and the reverse (13, 14), socket
  listener with rate limiting (22), retired sections (62; 200 for version deprecation), deprecation
  policies with `ReportApiVersions` off (201). Stable event IDs for every log message.
- **Metrics**: edge rejections, rejected correlation IDs, partition overflow.
- Trimming and Native AOT compatibility; `SECURITY.md`; package icons and READMEs.

### Changed

- **Licence**: proprietary, internal use only (was MIT).
- **429 and timeout bodies** go through your `IProblemDetailsService`; without one they are written
  with source-generated JSON, so they work under Native AOT.
- **A disabled feature leaves framework settings alone**; only Kestrel's `Server` header is removed
  regardless.
- **Your own `ICorsPolicyProvider` is wrapped**, not replaced.

### Breaking: code

- Registration methods renamed (`AddCors` → `AddApiPipelineCors`, …) and moved to Microsoft's
  namespaces; option types renamed (`CorsSettings` → `ApiPipelineCorsOptions`, `RateLimitPolicy` →
  `ApiPipelineRateLimitPolicy`, …) and moved from `ApiPipeline.NET.Options` to `ApiPipeline.NET`.
- `UseApiPipeline` extends `IApplicationBuilder`; `IApiPipelineBuilder` removed.
- Removed: `Skip*()`, `ApiPipelineServiceRegistrationOptions`, single-feature `Use*()`, request
  validation, public middleware types, the built-in exception handler,
  `RateLimitPolicy.AutoReplenishment`, and the `ApiPipeline.NET.OpenTelemetry` package.
- Version deprecation moved to `ApiPipeline.NET.Versioning` on `Asp.Versioning.Http`.
- Option collections are `IList<T>`; correlation IDs via `GetCorrelationId()`/`GetTraceId()`.
- Binding a feature to a second, different section throws.

### Breaking: configuration

- Sections moved under `ApiPipeline` without the `Options` suffix; old sections are not read.
- Durations are `TimeSpan`s without unit suffixes (`WindowSeconds` → `Window`); old keys and bare
  numbers fail startup.
- Settings are read once, at startup.
- A configured list replaces its default; `[]` is an empty list.
- `SuppressServerHeader` moved to `RequestLimits`; `PreferOutputCaching`, `PathPrefix` and
  `AutoReplenishment` removed.
- New startup failures, each naming the setting: unsafe proxy trust or `ProcessHost` (outside
  Development; flag renamed `EnforceTrustedProxyConfiguration`); CORS origins that can't match,
  unsafe credentials or a conflicting default policy; invalid rate limit policies (including
  `SegmentsPerWindow` above 1000), API keys without hashes, policies renamed by position, bad
  `ExcludedPaths`; HSTS preload rules; both caches or no codec; invalid deprecated versions, dates
  or links; undefined enum numbers; unsafe pipeline order; unregistered phases; a feature's own
  section passed in (in all three packages, `ArgumentException`). A `KnownProxies`, `KnownNetworks`
  or `ProbeNetworks` entry that doesn't parse, or writes IPv4 in shorthand, octal or hex
  (`10.0.10`, `010.0.0.1`, `10/8`), fails startup in every environment; such entries were skipped
  (events 50 and 51, now retired).

### Breaking: behavior

- **Framework options you set win**: the package's Kestrel, IIS, HTTP.sys, form, HSTS, compression,
  caching and timeout settings are defaults, applied before yours.
- **Security headers** are on by default and run early; new defaults CORP `same-origin`, CSP
  `default-src 'none'; frame-ancestors 'none'`, `Cache-Control: no-store`. HSTS via `UseHsts`.
- **Pipeline order** is the call order; broken rules are logged, security ones fail startup outside
  Development. Routing is added automatically.
- **Rate limiting**: named policies aren't capped by the default; IETF `RateLimit` fields replace
  `X-RateLimit-*` (off by default); token buckets advertise their refill rate; sliding windows send
  `Retry-After`; rejections stay 429 with your own `OnRejected`.
- **Correlation**: generated IDs are GUIDs; span tag `apipipeline.correlation_id`.
- **Deprecation** headers follow RFC 9745 and come from `Asp.Versioning`: they need
  `ReportApiVersions`, and short-circuited responses get none.
- **Metrics** renamed and removed: see [migration](docs/migration.md#5-telemetry-changes).

### Deprecated

- **Response caching** (`WithResponseCaching()`, `AddApiPipelineResponseCaching()`,
  `ApiPipeline:ResponseCaching`) warns
  [`APIPIPELINE001`](docs/diagnostics.md#apipipeline001); use output caching. Removed in the next
  major version.

### Fixed

- `AnonymousFallback: Reject` returns 429, not 500.
- Policies added in code are registered.
- Invalid policies such as `PermitLimit: 0` fail startup.
- Package settings survive the application configuring compression or response caching itself.
- `Cors:Enabled: false` keeps the application's default CORS policy.
- A disabled deprecation section no longer fails startup.
- `/health` compression exclusions can be removed.
- `Vary: Origin` is added when CORS and response caching are both on.
- Non-standard HTTP methods are reported as `_OTHER`.

### Security

- Startup fails on empty or loopback-only proxy trust, or a `KnownNetworks` entry that contains
  every client address (`0.0.0.0/0`, `::/0`, `::ffff:0:0/96`), outside Development;
  `X-Forwarded-Host` needs `ProcessHost` and `AllowedHosts`.
- API key partitions need an allow-list; IPv6 is partitioned by `/64`.
- CORS refuses credentials with `*` or `http://` origins outside Development.
- Redis keys are keyed hashes and don't reveal addresses or user IDs.
- Compression over HTTPS stays opt-in (BREACH).
- Startup checks run before the server listens, even with concurrent hosted-service start or a
  generic host; previously a failed guard could let the server serve requests first.
- CI and releases verify every package ID maps only to the private feed (dependency confusion); the
  installation guide recommends lock files.

### Build

- One version for all packages; package validation against the previous release.
- Pinned SDK (10.0.404, runtime 10.0.15; 10.0.14 first fixed CVE-2026-111497), locked restores,
  digest-pinned test images.
- xUnit v3 on Microsoft.Testing.Platform with coverage gates; Native AOT and Markdown checks in CI.

## [1.1.0] - 2026-05-27

Same commit as `v1.0.0`; contents beyond the original feature list:

### Added

- HSTS `preload`, `X-Frame-Options`, optional CSP and `Permissions-Policy`.
- `AnonymousFallback` (default `Reject`).
- `EnforceTrustedProxyConfigurationInProduction`.
- `PreferOutputCaching`, `ApiPipelineServiceRegistrationOptions`, `Skip*()` methods.
- `WithRequestSizeTracking()` and enforced phase ordering.
- `policy_name` and `partition_type` on `apipipeline.ratelimit.rejected`.

### Changed

- **Breaking:** section `SecurityHeaders` renamed `SecurityHeadersOptions`.
- CORS `AllowedHeaders` default changed from `["*"]` to `["Content-Type", "Authorization", "X-Correlation-Id"]`.

## [1.0.0] - 2026-05-27

Initial release.

### Added

- `AddApiPipeline(...)` and `UseApiPipeline(...)`.
- Rate limiting (fixed window, sliding window, concurrency, token bucket).
- Response compression (Brotli, Gzip) and response caching.
- Security headers (HSTS, `X-Content-Type-Options`, `Referrer-Policy`).
- CORS, correlation IDs, forwarded headers, Kestrel and form limits.
- API version deprecation headers.
- Optional `ApiPipeline.NET.OpenTelemetry` package.
