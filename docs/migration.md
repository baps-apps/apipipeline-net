# Migration Guide

This guide summarizes common upgrade paths and API wiring changes in `ApiPipeline.NET`.

## Per-feature registration to aggregate registration

If your app currently registers each feature separately:

```csharp
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
    .AddRequestSizeTracking();
```

You can now use:

```csharp
builder.Services.AddApiPipeline(builder.Configuration);
```

`AddApiPipeline(...)` already includes exception handling service registration, so an extra `AddApiPipelineExceptionHandler()` call is not required.

Use the optional callback to disable specific registrations:

```csharp
builder.Services.AddApiPipeline(builder.Configuration, options =>
{
    options.AddResponseCaching = false;
    options.AddRequestSizeTracking = false;
});
```

## Forwarded headers hardening

- In production, forwarded headers now fail fast by default when:
  - `ForwardedHeadersOptions:Enabled = true`
  - `ClearDefaultProxies = false`
  - both `KnownProxies` and `KnownNetworks` are empty
- Fix by configuring trusted proxies/networks and, for cloud ingress, typically setting `ClearDefaultProxies = true`.
- Temporary opt-out (not recommended): `EnforceTrustedProxyConfigurationInProduction = false`.

## CORS validation tightening

- When `CorsOptions:Enabled = true` and `AllowAllInDevelopment = false`:
  - `AllowedMethods` must contain at least one non-empty value.
  - `AllowedHeaders` must contain at least one non-empty value.
- Use explicit `["*"]` only when wildcard behavior is intentional.

## Rate limiting anonymous fallback

- Recommended baseline is `AnonymousFallback = Reject`.
- `RateLimit` fallback uses a shared anonymous bucket and is vulnerable to bucket exhaustion when client IP cannot be resolved.

## Versioning package integration

- `ApiVersionDeprecationMiddleware` emits deprecation headers only when an `IApiVersionReader` is registered.
- Use the `ApiPipeline.NET.Versioning` satellite package (or your own implementation) to provide this reader.
