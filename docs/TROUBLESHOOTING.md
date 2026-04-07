# Troubleshooting

Common issues and quick diagnostics for `ApiPipeline.NET`. Each section shows the symptom, likely cause, example error message, and fix.

---

## Startup validation failure

### Symptom

Application crashes on startup with `OptionsValidationException`.

### Example error messages

```text
Microsoft.Extensions.Options.OptionsValidationException:
  DataAnnotation validation failed for 'RateLimitingOptions' members:
    'DefaultPolicy' with the error: 'DefaultPolicy 'strict' does not match any configured policy name.'
```

```text
Microsoft.Extensions.Options.OptionsValidationException:
  DataAnnotation validation failed for 'CorsSettings' members:
    'AllowedOrigins' with the error: 'AllowCredentials cannot be used with wildcard origins.'
```

```text
Microsoft.Extensions.Options.OptionsValidationException:
  DataAnnotation validation failed for 'ForwardedHeadersSettings' members:
    'KnownNetworks' with the error: 'Production requires trusted proxy configuration...'
```

### Common causes

| Error pattern | Fix |
|---|---|
| `DefaultPolicy '...' does not match any configured policy name` | Ensure `RateLimitingOptions.DefaultPolicy` matches a `Policies[].Name` |
| `AllowCredentials cannot be used with wildcard origins` | Set explicit `AllowedOrigins` when `AllowCredentials: true` |
| `At least one policy must be configured` | Add at least one policy to `RateLimitingOptions.Policies` when `Enabled: true` |
| `Production requires trusted proxy configuration` | Set `KnownProxies` or `KnownNetworks`, or set `EnforceTrustedProxyConfigurationInProduction: false` |
| `WindowSeconds is required for FixedWindow policies` | Add `WindowSeconds` to your rate limit policy |

### Fix

1. Read the full `OptionsValidationException` message — it identifies the exact field and constraint.
2. Compare your config against the [README configuration examples](../README.md#configuration).
3. Correct config and restart.

---

## Rate limiting appears ineffective

### Symptom

Requests are not being rate-limited even though `RateLimitingOptions.Enabled: true`.

### Diagnostic checklist

```bash
# Verify config is loaded
dotnet user-secrets list  # check for overrides
cat appsettings.json | jq '.RateLimitingOptions'
```

| Check | Expected |
|---|---|
| `RateLimitingOptions:Enabled` | `true` |
| `DefaultPolicy` value | Matches a defined policy `Name` |
| `WithRateLimiting()` called | Present in `UseApiPipeline` callback |
| Forwarded headers configured | `WithForwardedHeaders()` runs before rate limiter when behind proxy |
| Test endpoint hit | Not excluded via named policy or `[DisableRateLimiting]` |

### Fix

```json
{
  "RateLimitingOptions": {
    "Enabled": true,
    "DefaultPolicy": "strict",
    "Policies": [
      {
        "Name": "strict",
        "Kind": "FixedWindow",
        "PermitLimit": 10,
        "WindowSeconds": 60,
        "QueueLimit": 0
      }
    ]
  }
}
```

---

## Too many false-positive `429`s

### Symptom

Legitimate users are being rate-limited prematurely.

### Common causes

1. **Proxy trust misconfiguration** — all clients share the proxy's IP, collapsing to one rate-limit bucket.
2. **`AnonymousFallback: "RateLimit"`** — unauthenticated requests share a single anonymous bucket.
3. **Queue limits too low** for bursty traffic patterns.

### Diagnostic steps

```bash
# Check if all requests use the same partition key (proxy IP)
# In application logs, look for rate limit partition keys
kubectl logs deploy/api-service | grep -i "partition"

# Verify forwarded headers are working
curl -H "X-Forwarded-For: 1.2.3.4" https://api.example.com/health -v 2>&1 | grep -i "x-forwarded"
```

### Fix

```json
{
  "ForwardedHeadersOptions": {
    "Enabled": true,
    "ClearDefaultProxies": true,
    "KnownNetworks": ["10.0.0.0/8"]
  },
  "RateLimitingOptions": {
    "AnonymousFallback": "Reject",
    "Policies": [{
      "Name": "strict",
      "PermitLimit": 100,
      "WindowSeconds": 60,
      "QueueLimit": 5
    }]
  }
}
```

---

## Missing correlation ID in responses/logs

### Symptom

`X-Correlation-Id` header is absent from responses, or log entries lack correlation context.

### Diagnostic checklist

| Check | Expected |
|---|---|
| `WithCorrelationId()` called | Present in `UseApiPipeline` callback |
| Upstream proxy strips headers | Ensure proxy does not strip `X-Correlation-Id` |
| Logging enricher configured | Serilog enricher or `ILogger.BeginScope` includes correlation ID |

### Verify

```bash
# Send a request with a known correlation ID
curl -H "X-Correlation-Id: test-123" https://api.example.com/health -v 2>&1 | grep -i "x-correlation-id"

# Response should echo: X-Correlation-Id: test-123
# If the value changes, input validation rejected the original (check format: alphanumeric, hyphens, underscores, dots, max 128 chars)
```

### Reading correlation ID in your code

```csharp
var correlationId = httpContext.Items["CorrelationId"]?.ToString();
```

---

## CORS preflight rejected

### Symptom

Browser shows: `Access to fetch at '...' from origin '...' has been blocked by CORS policy`.

### Diagnostic steps

```bash
# Simulate a preflight request
curl -X OPTIONS https://api.example.com/api/resource \
  -H "Origin: https://app.example.com" \
  -H "Access-Control-Request-Method: POST" \
  -H "Access-Control-Request-Headers: Content-Type, Authorization" \
  -v 2>&1 | grep -i "access-control"
```

### Checklist

| Check | Expected |
|---|---|
| Request origin in `AllowedOrigins` | Exact match including scheme and port |
| `OPTIONS` in `AllowedMethods` | Present (or method wildcard `*`) |
| Required headers in `AllowedHeaders` | `Content-Type`, `Authorization`, custom headers listed |
| `AllowCredentials` + origins | Must have explicit origins (no wildcard) |

### Fix

```json
{
  "CorsOptions": {
    "Enabled": true,
    "AllowedOrigins": ["https://app.example.com", "https://staging.example.com"],
    "AllowedMethods": ["GET", "POST", "PUT", "PATCH", "DELETE", "OPTIONS"],
    "AllowedHeaders": ["Content-Type", "Authorization", "X-Correlation-Id"],
    "AllowCredentials": true
  }
}
```

---

## Security headers not present

### Symptom

Response headers are missing `X-Content-Type-Options`, `Referrer-Policy`, or `Strict-Transport-Security`.

### Diagnostic steps

```bash
# Check response headers
curl -s -D - https://api.example.com/health | grep -iE "x-content-type|referrer-policy|strict-transport|x-frame-options"
```

### Checklist

| Check | Expected |
|---|---|
| `SecurityHeadersOptions:Enabled` | `true` |
| `WithSecurityHeaders()` called | Present in `UseApiPipeline` callback |
| HSTS in development | Automatically skipped — check in staging/production |
| Another middleware overwriting | Check for downstream middleware that modifies headers |

### Fix

```json
{
  "SecurityHeadersOptions": {
    "Enabled": true,
    "ReferrerPolicy": "no-referrer",
    "AddXContentTypeOptionsNoSniff": true,
    "EnableStrictTransportSecurity": true,
    "StrictTransportSecurityMaxAgeSeconds": 31536000,
    "StrictTransportSecurityIncludeSubDomains": true,
    "AddXFrameOptions": true,
    "XFrameOptionsValue": "DENY"
  }
}
```

---

## Deprecation headers not emitted

### Symptom

Requests to deprecated API versions do not include `Deprecation`, `Sunset`, or `Link` response headers.

### Diagnostic steps

```bash
# Check for deprecation headers on a versioned route
curl -s -D - https://api.example.com/api/v1/resource | grep -iE "deprecation|sunset|link"
```

### Checklist

| Check | Expected |
|---|---|
| `ApiVersionDeprecationOptions:Enabled` | `true` |
| Request path starts with `PathPrefix` | Default is `/api` |
| Requested version in `DeprecatedVersions` | `Version` field matches (e.g., `"1.0"`) |
| `IApiVersionReader` registered | Install `ApiPipeline.NET.Versioning` or implement custom reader |
| `WithVersionDeprecation()` called | Present in `UseApiPipeline` callback |

### Fix

```json
{
  "ApiVersionDeprecationOptions": {
    "Enabled": true,
    "PathPrefix": "/api",
    "DeprecatedVersions": [
      {
        "Version": "1.0",
        "DeprecationDate": "2025-07-01T00:00:00Z",
        "SunsetDate": "2026-01-01T00:00:00Z",
        "SunsetLink": "https://docs.example.com/api-v1-migration"
      }
    ]
  }
}
```

---

## Response compression not working

### Symptom

API responses are not compressed even with `ResponseCompressionOptions.Enabled: true`.

### Diagnostic steps

```bash
# Send request with Accept-Encoding
curl -s -D - -H "Accept-Encoding: br, gzip" https://api.example.com/api/resource | grep -i "content-encoding"
```

### Checklist

| Check | Expected |
|---|---|
| `ResponseCompressionOptions:Enabled` | `true` |
| `WithResponseCompression()` called | Present in `UseApiPipeline` callback |
| Request path not in `ExcludedPaths` | `/health` is excluded by default |
| Response MIME type in `MimeTypes` | `application/json`, `application/problem+json` |
| Client sends `Accept-Encoding` | Header must include `br` or `gzip` |
| HTTPS compression enabled | `EnableForHttps: true` (consider BREACH risk) |

---

## Still stuck?

- Check [RUNBOOK.md](RUNBOOK.md) for incident-specific procedures.
- Check [OPERATIONS.md](OPERATIONS.md) for production baseline configuration.
- Compare your config against the [README configuration examples](../README.md#configuration).
- Open an issue with your configuration (redact secrets) and the full error message.
