# Changelog

All notable changes to **ApiPipeline.NET** are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project follows [Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added

- `StrictTransportSecurityPreload` option for HSTS preload directive.
- `AddXFrameOptions` / `XFrameOptionsValue` for clickjacking protection.
- `ContentSecurityPolicy` and `PermissionsPolicy` optional security headers.
- `AnonymousFallback` option on `RateLimitingOptions` (default: `Reject`).
- `EnforceTrustedProxyConfigurationInProduction` on `ForwardedHeadersSettings`.
- `PreferOutputCaching` migration hint on `ResponseCachingSettings`.
- `ApiPipelineServiceRegistrationOptions` for selective feature registration.
- Full `Skip*()` API: `SkipCorrelationId()`, `SkipExceptionHandler()`, `SkipHttpsRedirection()`, `SkipVersionDeprecation()`, `SkipSecurityHeaders()`, `SkipCors()`, `SkipResponseCompression()`, `SkipResponseCaching()`, `SkipRateLimiting()`, `SkipForwardedHeaders()`, `SkipRequestValidation()`, `SkipAuthentication()`, `SkipAuthorization()`, `SkipRequestSizeTracking()`.
- `WithRequestSizeTracking()` on pipeline builder (no longer requires standalone `UseRequestSizeTracking()` call).
- Phase-enforced middleware ordering in `ApiPipelineBuilder`.
- Rate-limit `OnRejected` telemetry now includes `policy_name` and `partition_type` dimensions.
- `FrozenDictionary` for immutable rate-limit policy snapshot.

### Changed

- **BREAKING**: Configuration section `SecurityHeaders` renamed to `SecurityHeadersOptions` for naming consistency.
- CORS `AllowedHeaders` default changed from `["*"]` to `["Content-Type", "Authorization", "X-Correlation-Id"]`.
- Rate limiter uses `IOptionsMonitor` (singleton) instead of `IOptionsSnapshot` (per-request) in hot path.
- `CorrelationIdMiddleware` uses zero-alloc struct scope instead of per-request `KeyValuePair[]` allocation.
- Removed no-op `[MinLength(0)]` from `CorsSettings` array properties.

## [1.0.0] - Initial release

### Added

- Shared API middleware pipeline via `AddApiPipeline(...)` and `UseApiPipeline(...)`.
- Rate limiting with named policies (`FixedWindow`, `SlidingWindow`, `Concurrency`, `TokenBucket`) and RFC 7807 rejection responses.
- Response compression (`Brotli`, `Gzip`) and path exclusions.
- Response caching registration and middleware integration.
- Security header middleware (`HSTS`, `X-Content-Type-Options`, `Referrer-Policy`) with development-safe behavior.
- CORS configuration for development and production modes.
- Correlation ID middleware with strict input validation and response echoing.
- API version deprecation headers (`Deprecation`, `Sunset`, `Link`) for configured versions.
- Request limits integration for Kestrel and form options.
- Forwarded headers hardening for reverse proxy and ingress deployments.
- Structured exception handling with RFC 7807 `ProblemDetails` and correlation/trace enrichment.
- Optional OpenTelemetry package (`ApiPipeline.NET.OpenTelemetry`) for tracing, metrics, and logging setup.
- Sample application in `samples/ApiPipeline.NET.Sample`.
- Unit/integration-style tests in `tests/ApiPipeline.NET.Tests`.
- Baseline migration and performance guides (`docs/migration.md`, `docs/performance.md`).
