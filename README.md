# ApiPipeline.NET

[![.NET 10](https://img.shields.io/badge/.NET-10-512BD4)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![License: Proprietary, internal use only](https://img.shields.io/badge/License-Proprietary%20(internal%20use%20only)-red.svg)](LICENSE)

A shared ASP.NET Core middleware pipeline for APIs. You choose the features and their order; the
package checks the order and validates the configuration at startup.

- **Rate limiting**: named policies; per user, API key, IP, tenant or client; an edge limiter before
  authentication; IETF `RateLimit` headers
- **Security headers**: HSTS, `nosniff`, `Referrer-Policy`, `X-Frame-Options`, CORP, default
  `Cache-Control: no-store` and `Content-Security-Policy`
- **CORS**, **correlation IDs**, **forwarded headers**
- **Compression**, **output caching**, **request limits**, **request timeouts**
- **Metrics** on a standard .NET `Meter`

Optional packages: [`ApiPipeline.NET.Versioning`](src/ApiPipeline.NET.Versioning/README.md)
(deprecation headers) and [`ApiPipeline.NET.RateLimiting.Redis`](src/ApiPipeline.NET.RateLimiting.Redis/README.md)
(limits shared across pods).

## Installation

The packages are on a private feed. Commit this `nuget.config` next to your solution:

```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
    <add key="baps-apps-packages" value="https://nuget.pkg.github.com/baps-apps/index.json" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="baps-apps-packages">
      <package pattern="ApiPipeline.NET" />
      <package pattern="ApiPipeline.NET.*" />
    </packageSource>
    <packageSource key="nuget.org">
      <package pattern="*" />
    </packageSource>
  </packageSourceMapping>
</configuration>
```

- **Keep the mapping.** These IDs are not reserved on nuget.org; without it, a package published
  there under the same ID could be restored (dependency confusion). Both patterns are needed.
- **Use lock files.** `<RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>`, commit
  `packages.lock.json`, and restore in CI with `--locked-mode`.
- **Credentials** (a PAT with `read:packages`) go in the user-level `NuGet.Config` under
  `<packageSourceCredentials><baps-apps-packages>`, never in the repository and without adding the
  source there. In CI, use `dotnet nuget update source`.

```bash
dotnet add package ApiPipeline.NET
dotnet add package ApiPipeline.NET.Versioning           # optional
dotnet add package ApiPipeline.NET.RateLimiting.Redis   # optional
```

## Quick start

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddApiPipeline(builder.Configuration);   // reads "ApiPipeline"
builder.Services.AddAuthentication(/* your scheme */);
builder.Services.AddAuthorization();
builder.Services.AddProblemDetails();
builder.Services.AddControllers();

var app = builder.Build();

app.UseApiPipeline(pipeline => pipeline
    .RequireSecurityFeaturesEnabled()   // outside Development, a security phase left off fails startup
    .WithForwardedHeaders()
    .WithCorrelationId()
    .WithSecurityHeaders()
    .WithMiddleware(a => { a.UseExceptionHandler(); a.UseStatusCodePages(); })
    .WithRequestTimeouts()
    .WithCors()
    .WithEdgeRateLimiting()
    .WithAuthentication()
    .WithAuthorization()
    .WithRateLimiting()
    .WithResponseCompression()
    .WithOutputCaching());

app.MapControllers();
app.Run();
```

How it behaves:

- **A feature needs both switches**: its `With*()` call and `Enabled: true`. Leave out the call to
  opt out. Either half alone logs a warning (events 15 and 13).
- **Make a disabled security feature fail startup** with `.RequireSecurityFeaturesEnabled()`.
  Outside Development, `WithForwardedHeaders()`, `WithSecurityHeaders()`, `WithRequestTimeouts()`,
  `WithEdgeRateLimiting()` or `WithRateLimiting()` with its feature's `Enabled: false` then fails
  instead of logging event 15. Opt-in; recommended for production APIs.
- **You decide the order.** Nothing is reordered. A [broken rule](#pipeline-order) logs a warning; a
  broken security rule fails startup outside Development.
- **`With*()` needs its registration** (`AddApiPipeline` or `AddApiPipeline<Feature>`); otherwise
  building the pipeline fails.
- **`WithMiddleware(...)` adds your middleware at that position**, unchecked.
- **Routing is added** after forwarded headers unless you call `WithRouting()`.
- **The composed order is logged** at startup (`ApiPipeline.NET pipeline composed ...`).
- **No `using` needed** for the extension methods; option types are in `ApiPipeline.NET`.

## Pipeline order

| # | Phase | Why here |
| --- | --- | --- |
| 1 | `WithForwardedHeaders()` | Real client IP and scheme for everything after |
| – | Routing (automatic or `WithRouting()`) | Later phases see the endpoint |
| 2 | `WithRequestSizeTracking()` | Optional body-size metric |
| 3 | `WithCorrelationId()` | IDs exist before anything can fail |
| 4 | `WithSecurityHeaders()` | 401/403/429/504 get the headers too |
| 5 | `WithRequestTimeouts()` | Covers authentication; a 504 has the headers |
| 6 | `WithHttpsRedirection()` | Only if TLS isn't terminated at the ingress |
| 7 | `WithCors()` | Preflights answered before auth and limits |
| 8 | `WithEdgeRateLimiting()` | Sheds floods before token validation |
| 9 | `WithAuthentication()`, `WithAuthorization()` | |
| 10 | `WithRateLimiting()` | Partitions by user |
| 11 | `WithResponseCompression()` | |
| 12 | `WithOutputCaching()` | After auth, so private responses aren't shared |

## Features and defaults

| Feature | Section under `ApiPipeline` | `Enabled` default | Guide |
| --- | --- | --- | --- |
| Correlation IDs | `CorrelationId` | on with `WithCorrelationId()` | [correlation-id.md](docs/features/correlation-id.md) |
| Security headers | `SecurityHeaders` | `true` | [security-headers.md](docs/features/security-headers.md) |
| Forwarded headers | `ForwardedHeaders` | `true` | [forwarded-headers.md](docs/features/forwarded-headers.md) |
| CORS | `Cors` | `false` | [cors.md](docs/features/cors.md) |
| Rate limiting | `RateLimiting` | `false` | [rate-limiting.md](docs/features/rate-limiting.md) |
| Edge rate limiting | `EdgeRateLimiting` | `false` | [rate-limiting.md](docs/features/rate-limiting.md#edge-rate-limiting) |
| Shared limits (Redis) | `RateLimiting:Redis` | `false` | [rate-limiting.md](docs/features/rate-limiting.md#shared-limits-across-pods) |
| Request timeouts | `RequestTimeouts` | `false` | [request-timeouts.md](docs/features/request-timeouts.md) |
| Request limits | `RequestLimits` | `false` | [request-limits.md](docs/features/request-limits.md) |
| Response compression | `ResponseCompression` | `false` | [response-compression.md](docs/features/response-compression.md) |
| Output caching | `OutputCaching` | `false` | [output-caching.md](docs/features/output-caching.md) |
| Response caching (obsolete) | `ResponseCaching` | `false` | [response-caching.md](docs/features/response-caching.md) |
| Version deprecation | `VersionDeprecation` | `false` | [version-deprecation.md](docs/features/version-deprecation.md) |

Only features that never reject a request are on by default. Forwarded headers fail startup outside
Development until you name your proxies. Anything that can reject, delay or change responses is off
until configured, and so is CORS, which loosens what browsers allow.

Watch for:

- **Limits and caches are per pod** unless you add the Redis package or a distributed output
  cache store.
- **Compression skips HTTPS** unless `EnableForHttps` is set, which behind a TLS-terminating proxy
  means nothing is compressed.
- **A named rate limit policy replaces the default** on its endpoint.

## Configuration

A production `appsettings.json` that starts as-is:

```json
{
  "ApiPipeline": {
    "ForwardedHeaders": {
      "KnownNetworks": [ "10.0.10.0/24" ],
      "ClearDefaultProxies": true
    },
    "SecurityHeaders": {
      "PermissionsPolicy": "camera=(), microphone=(), geolocation=()"
    },
    "RequestTimeouts": { "Enabled": true, "DefaultTimeout": "00:00:30" },
    "Cors": {
      "Enabled": true,
      "AllowedOrigins": [ "https://app.example.com" ],
      "AllowCredentials": true
    },
    "EdgeRateLimiting": { "Enabled": true, "PermitLimit": 600, "Window": "00:01:00" },
    "RateLimiting": {
      "Enabled": true,
      "DefaultPolicy": "per-user",
      "Policies": [
        { "Name": "per-user", "Kind": "FixedWindow", "PermitLimit": 100, "Window": "00:01:00" },
        { "Name": "expensive", "Kind": "TokenBucket", "PermitLimit": 10, "Window": "00:01:00", "TokensPerPeriod": 5 }
      ]
    },
    "OutputCaching": { "Enabled": true },
    "RequestLimits": {
      "Enabled": true,
      "MaxRequestBodySize": 10485760,
      "RequestHeadersTimeout": "00:00:15",
      "MaxConcurrentConnections": 2000
    }
  }
}
```

- **`KnownNetworks`** must be your ingress's own addresses, as narrow as possible: every address in
  it can set the client IP.
- **In code**, every feature has an `Action<TOptions>` overload:
  `AddApiPipelineRateLimiting(o => { o.Enabled = true; ... })`.
- **Elsewhere in configuration**: `AddApiPipeline(configuration.GetSection("Platform:Api"))` reads
  `Platform:Api:Cors` and so on.
- **Durations** are `TimeSpan`s: `"00:01:00"`, `"00:00:00.250"`. A bare number fails startup.
- **Validation** runs at startup; an invalid setting fails it with a message naming the setting.
- **Development**: `"Cors": { "AllowAllInDevelopment": true }` in `appsettings.Development.json`.

### Configuration is read once

Settings are read at startup, like ASP.NET Core's middleware options; a change applies on restart.
An invalid edit never reaches a running pod: the next one fails to start, stopping the rollout.

## Error handling

The package writes RFC 9457 bodies only for its own 429s and timeouts, through your
`IProblemDetailsService`. Add the IDs to every error body:

```csharp
builder.Services.AddProblemDetails(options =>
{
    options.CustomizeProblemDetails = context =>
    {
        context.HttpContext.Response.Headers.CacheControl = "no-store";

        if (context.HttpContext.GetCorrelationId() is { Length: > 0 } correlationId)
            context.ProblemDetails.Extensions["correlationId"] = correlationId;

        // Replaces ASP.NET Core's traceparent with the plain trace ID, matching X-Trace-Id.
        if (context.HttpContext.GetTraceId() is { Length: > 0 } traceId)
            context.ProblemDetails.Extensions["traceId"] = traceId;
    };
});

app.UseApiPipeline(pipeline => pipeline
    .WithForwardedHeaders()
    .WithCorrelationId()
    .WithSecurityHeaders()
    .WithMiddleware(a =>
    {
        a.UseExceptionHandler();   // inside the correlation scope: its log line has CorrelationId
        a.UseStatusCodePages();
    })
    /* ... */);
```

`GetCorrelationId()` and `GetTraceId()` are in `ApiPipeline.NET.Correlation`. An exception handler
added before `UseApiPipeline` logs outside the correlation scope. For validation, use
`[ApiController]`, `AddValidation()` or an `IEndpointFilter`.

## Security notes

The package does not authenticate anyone; keep your WAF, TLS and identity provider. It enforces:
proxy trust ([forwarded headers](docs/features/forwarded-headers.md)), rate limit partitions
([rate limiting](docs/features/rate-limiting.md#who-shares-a-bucket)), CORS origin checks
([CORS](docs/features/cors.md)), safe response headers
([security headers](docs/features/security-headers.md)) and validated correlation IDs
([correlation IDs](docs/features/correlation-id.md)). Report vulnerabilities per
[SECURITY.md](SECURITY.md).

## Production checklist

| Area | Do this |
| --- | --- |
| Forwarded headers | `KnownNetworks`/`KnownProxies` for your ingress, `ClearDefaultProxies: true` |
| HTTPS | Terminate TLS at the ingress or call `WithHttpsRedirection()` |
| CORS | Exact `AllowedOrigins` |
| Rate limiting | Size per pod or add Redis; global limits at the ingress; edge limiting on public APIs |
| Redis fallbacks | `PolicyFallbacks: Reject` for login, OTP, payments; the default `PerPod` allows N× the limit during an outage |
| Timeouts | Enable `RequestTimeouts`; `.DisableRequestTimeout()` on streams |
| Slow clients | `RequestHeadersTimeout`, `MaxConcurrentConnections`, `MinRequestBodyDataRateBytesPerSecond` |
| Errors | `AddProblemDetails()` and the [exception handler inside the pipeline](#error-handling) |
| Health probes | List exact probe paths in `ExcludedPaths` and your node network in `ProbeNetworks`: by default every private address is exempt under `/health`, including outside clients behind a load balancer that rewrites their address ([details](docs/features/rate-limiting.md#health-probes)) |
| Metrics | Export the `ApiPipeline.NET` meter |

## Observability

```csharp
builder.Services.AddOpenTelemetry()
    .WithMetrics(m => m.AddMeter(ApiPipelineTelemetry.MeterName));   // "ApiPipeline.NET"
```

Request logs carry `CorrelationId` and `TraceId` in their scope. Metrics, log events and alerts:
[docs/OBSERVABILITY.md](docs/OBSERVABILITY.md).

## Versioning and compatibility

- `net10.0`; trimming and Native AOT compatible.
- [Semantic Versioning](docs/VERSIONING.md). Upgrading from 1.x: [migration guide](docs/migration.md).
- Inbound only. For outbound resilience use
  [HttpResilience.NET](https://github.com/baps-apps/http-resilience-net/pkgs/nuget/HttpResilience.NET)
  or `Microsoft.Extensions.Http.Resilience`.

## More docs

[CHANGELOG](CHANGELOG.md) · [Migration](docs/migration.md) · [Architecture](docs/ARCHITECTURE.md) ·
[Observability](docs/OBSERVABILITY.md) · [Operations](docs/OPERATIONS.md) ·
[Runbook](docs/RUNBOOK.md) · [Troubleshooting](docs/TROUBLESHOOTING.md) ·
[Performance](docs/performance.md) · [Contributing](CONTRIBUTING.md) · [Security](SECURITY.md)
