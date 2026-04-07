# ApiPipeline.NET Operations Guide

Operational guidance for running services that use `ApiPipeline.NET` in production.

## Runtime configuration

- Most behavior is controlled through `appsettings.*.json` (or equivalent config providers).
- Changes to options generally require app restart unless your host supports hot reload and your service lifecycle picks up new values.
- Treat proxy trust settings and rate limiting policy changes as **high-impact** operations — test in staging first.

## Recommended production baseline

### Forwarded headers (required behind proxy/ingress)

```json
{
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

Without `ClearDefaultProxies: true` and a configured `KnownNetworks` or `KnownProxies`, `X-Forwarded-For` headers are ignored and all traffic shares the proxy's IP. This collapses rate limiting to a single bucket.

### CORS

```json
{
  "CorsOptions": {
    "Enabled": true,
    "AllowAllInDevelopment": false,
    "AllowedOrigins": ["https://app.example.com"],
    "AllowedMethods": ["GET", "POST", "PUT", "PATCH", "DELETE", "OPTIONS"],
    "AllowedHeaders": ["Content-Type", "Authorization", "X-Correlation-Id"],
    "AllowCredentials": true
  }
}
```

Never use wildcard origins in production. When `AllowCredentials` is `true`, explicit origins are required (CORS spec).

### Rate limiting

```json
{
  "RateLimitingOptions": {
    "Enabled": true,
    "AnonymousFallback": "Reject",
    "DefaultPolicy": "per-user",
    "Policies": [
      {
        "Name": "per-user",
        "Kind": "FixedWindow",
        "PermitLimit": 100,
        "WindowSeconds": 60,
        "QueueLimit": 0,
        "QueueProcessingOrder": "OldestFirst",
        "AutoReplenishment": true
      }
    ]
  }
}
```

Use `AnonymousFallback: "Reject"` in production. The `"RateLimit"` fallback uses a shared anonymous bucket vulnerable to single-client exhaustion.

### Request limits

```json
{
  "RequestLimitsOptions": {
    "Enabled": true,
    "MaxRequestBodySize": 10485760,
    "MaxRequestHeadersTotalSize": 32768,
    "MaxRequestHeaderCount": 100,
    "MaxFormValueCount": 1024
  }
}
```

Set `MaxRequestBodySize` to match your API contract. Kestrel's default is ~30 MB; 10 MB (10485760) is a safer production baseline for most APIs.

### Security headers

```json
{
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
  }
}
```

Set `ContentSecurityPolicy` and `PermissionsPolicy` when your API domain serves browser-rendered content (e.g., Swagger UI, portal pages).

## Health check wiring

Map health endpoints and exclude them from compression and caching:

```csharp
app.MapGet("/health", () => Results.Ok(new { Status = "Healthy" }));
app.MapGet("/health/ready", () => Results.Ok());
app.MapGet("/health/live", () => Results.Ok());
```

The default `ResponseCompressionOptions.ExcludedPaths` already includes `/health`.

### Kubernetes probe configuration

```yaml
apiVersion: apps/v1
kind: Deployment
spec:
  template:
    spec:
      containers:
        - name: api
          livenessProbe:
            httpGet:
              path: /health/live
              port: 8080
            initialDelaySeconds: 10
            periodSeconds: 15
          readinessProbe:
            httpGet:
              path: /health/ready
              port: 8080
            initialDelaySeconds: 5
            periodSeconds: 10
```

## Metrics and observability

With `ApiPipeline.NET.OpenTelemetry`, the following are exported:

| Signal | What to monitor |
|---|---|
| Traces | Request flow, middleware boundaries, cross-service correlation |
| Metrics | Rate limit rejections, request sizes, security headers applied |
| Logs | Correlated via `X-Correlation-Id` and `Activity.TraceId` |

### Key metrics to track

| Metric / Signal | Query (Prometheus example) | Threshold |
|---|---|---|
| 429 rate | `rate(apipipeline_ratelimit_rejected_total[5m])` | Alert if > 10/min sustained |
| 5xx rate by route | `rate(http_server_request_duration_seconds_count{http_response_status_code=~"5.."}[5m])` | Alert if > 1% of traffic |
| p95 latency | `histogram_quantile(0.95, rate(http_server_request_duration_seconds_bucket[5m]))` | Alert if > baseline × 1.5 |
| Request body sizes | `histogram_quantile(0.99, rate(apipipeline_request_size_bytes_bucket[5m]))` | Capacity planning |
| Deprecation headers | `rate(apipipeline_deprecation_headers_added_total[1h])` | Track adoption of deprecated versions |

### Grafana dashboard essentials

- Rate limit rejection rate and burst shape (time series)
- 429/5xx breakdown by route and API version (table)
- Latency percentiles p50/p95/p99 (heatmap)
- Request body size distribution (histogram)

## Deployment checks

Before rollout:

```bash
# 1. Build and test
dotnet build
dotnet test

# 2. Validate production config (look for unsafe defaults)
grep -r '"AllowAllInDevelopment": true' appsettings.Production.json  # should not match
grep -r '"ClearDefaultProxies": false' appsettings.Production.json  # review if matched

# 3. Verify health endpoints
curl -s http://localhost:8080/health | jq .

# 4. Confirm no dev-only flags in production config
```

| Check | Command / Action |
|---|---|
| Build clean | `dotnet build --no-restore` |
| Tests pass | `dotnet test --no-build` |
| No dev CORS in prod | Verify `AllowAllInDevelopment` is `false` in production config |
| Proxy trust configured | `KnownNetworks` or `KnownProxies` populated for your topology |
| Health probes wired | Ingress probes target mapped health endpoints |
| Request limits set | `MaxRequestBodySize` matches API contract and ingress limits |

## Performance hygiene

- Run microbenchmarks with `perf/ApiPipeline.NET.Perf`:

```bash
dotnet run -c Release --project perf/ApiPipeline.NET.Perf
```

- Compare `MinimalPipeline_GetPing` vs `FullPipeline_GetPing` regularly.
- Treat p95 regression above 20% vs baseline as a release gate for middleware-heavy changes.
- See [performance.md](performance.md) for baseline benchmark commands and load test matrix.

## Security operations

- Rotate trusted proxy/IP ranges as infrastructure changes.
- Keep HSTS only for correct HTTPS deployments. Set `StrictTransportSecurityPreload: true` only when all subdomains support HTTPS.
- Review deprecation headers for legacy API versions and communicate sunset windows early.
- Audit `AllowedHeaders` in CORS — prefer explicit lists over `["*"]`.

## Incident entry points

- Resilience/traffic incidents: [RUNBOOK.md](RUNBOOK.md)
- Misconfiguration and unexpected behavior: [TROUBLESHOOTING.md](TROUBLESHOOTING.md)
