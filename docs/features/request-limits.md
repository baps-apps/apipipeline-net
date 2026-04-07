# Kestrel + Form Request Limits

## What it does

ApiPipeline.NET configures Kestrel server limits and ASP.NET Core form options from a single `RequestLimitsOptions` configuration section. This ensures consistent request size enforcement for both JSON and multipart/form-data payloads, protecting against oversized requests at the server level.

## Why it matters

- **DoS protection**: limits prevent oversized requests from consuming excessive memory and CPU.
- **Consistent enforcement**: the same body size limit applies to Kestrel (raw HTTP) and form options (multipart/form-data). Without this, Kestrel might accept a request that form parsing rejects, or vice versa.
- **Configuration-driven**: no code changes needed — set limits in `appsettings.json` and they apply to all endpoints.
- **Matches ingress limits**: your API's request limits should match or be stricter than your ingress/reverse proxy limits to avoid confusing error behavior.

## How it works

```text
appsettings.json → RequestLimitsOptions
  ↓
  ├── IConfigureOptions<KestrelServerOptions>
  │   ├── MaxRequestBodySize
  │   ├── MaxRequestHeadersTotalSize
  │   └── MaxRequestHeaderCount
  │
  └── IConfigureOptions<FormOptions>
      ├── MultipartBodyLengthLimit = MaxRequestBodySize
      ├── BufferBodyLengthLimit = MaxRequestBodySize
      └── ValueCountLimit = MaxFormValueCount
```

When `Enabled` is `false`, no limits are applied and Kestrel/form options use their framework defaults.

### Kestrel defaults vs recommended

| Limit | Kestrel default | Recommended production |
|---|---|---|
| `MaxRequestBodySize` | ~30 MB | 10 MB (`10485760`) |
| `MaxRequestHeadersTotalSize` | 32 KB | 32 KB (`32768`) |
| `MaxRequestHeaderCount` | 100 | 100 |

## Registration

```csharp
// Aggregate (recommended)
builder.Services.AddApiPipeline(builder.Configuration);

// Or standalone
builder.Services.AddRequestLimits(builder.Configuration);
```

No pipeline middleware is needed — limits are applied via `IConfigureOptions<KestrelServerOptions>` and `IConfigureOptions<FormOptions>` at startup.

## All available options

### `RequestLimitsOptions` (JSON section: `RequestLimitsOptions`)

| Property | Type | Default | Kestrel mapping | Description |
|---|---|---|---|---|
| `Enabled` | `bool` | `false` | — | Master switch. When `false`, no limits are applied. |
| `MaxRequestBodySize` | `long?` | `null` | `KestrelServerOptions.Limits.MaxRequestBodySize` | Maximum allowed request body size in bytes. Also applied to `FormOptions.MultipartBodyLengthLimit` and `FormOptions.BufferBodyLengthLimit`. Minimum: 1. |
| `MaxRequestHeadersTotalSize` | `int?` | `null` | `KestrelServerOptions.Limits.MaxRequestHeadersTotalSize` | Maximum total size of all request headers in bytes. |
| `MaxRequestHeaderCount` | `int?` | `null` | `KestrelServerOptions.Limits.MaxRequestHeaderCount` | Maximum number of request headers allowed. |
| `MaxFormValueCount` | `int?` | `null` | `FormOptions.ValueCountLimit` | Maximum number of form values in multipart/form-data requests. |

### Validation rules

| Constraint | Applied at |
|---|---|
| `MaxRequestBodySize >= 1` | Startup (via `[Range]` attribute) |
| `MaxRequestHeadersTotalSize >= 1` | Startup |
| `MaxRequestHeaderCount >= 1` | Startup |
| `MaxFormValueCount >= 1` | Startup |

Setting `MaxRequestBodySize` to `0` would silently reject all non-GET requests — the `[Range(1, ...)]` validation prevents this.

## Configuration examples

### Standard API (10 MB body limit)

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

### File upload API (100 MB body limit)

```json
{
  "RequestLimitsOptions": {
    "Enabled": true,
    "MaxRequestBodySize": 104857600,
    "MaxRequestHeadersTotalSize": 32768,
    "MaxRequestHeaderCount": 100,
    "MaxFormValueCount": 2048
  }
}
```

### Minimal API (small payloads only)

```json
{
  "RequestLimitsOptions": {
    "Enabled": true,
    "MaxRequestBodySize": 1048576,
    "MaxRequestHeadersTotalSize": 16384,
    "MaxRequestHeaderCount": 50,
    "MaxFormValueCount": 256
  }
}
```

1 MB body, 16 KB headers, 50 headers max, 256 form values max.

### Partial configuration (only body limit)

```json
{
  "RequestLimitsOptions": {
    "Enabled": true,
    "MaxRequestBodySize": 10485760
  }
}
```

Only `MaxRequestBodySize` is overridden. Other limits use Kestrel/form defaults.

## Production recommendations

| Recommendation | Why |
|---|---|
| Set `MaxRequestBodySize` to 10 MB or less for standard APIs | Kestrel default (~30 MB) is too permissive for most APIs |
| Match ingress limits | If your NGINX/Envoy has `client_max_body_size 10m`, set `MaxRequestBodySize` to `10485760` |
| Set `MaxFormValueCount` based on your largest form | Prevents form-bomb attacks with thousands of values |
| Use `Enabled: true` always in production | Explicit limits are better than framework defaults |
| Document endpoint-specific exceptions | If one endpoint needs larger limits, document and use `[RequestSizeLimit]` attribute |

### Per-endpoint overrides

For specific endpoints that need different limits:

```csharp
[RequestSizeLimit(104857600)] // 100 MB for this endpoint only
app.MapPost("/api/uploads", handler);

[DisableRequestSizeLimit] // No limit for this endpoint
app.MapPost("/api/stream", handler);
```

## Non-production recommendations

| Recommendation | Why |
|---|---|
| Use the same limits as production | Catches "request too large" issues during development |
| Reduce `MaxRequestBodySize` if not testing uploads | Catches accidental large payloads early |
| Keep `Enabled: true` | Consistent behavior between environments |

## Troubleshooting

### Requests rejected with 413 Payload Too Large

The request body exceeds `MaxRequestBodySize`. Check:

1. The actual request body size vs the configured limit.
2. Whether the endpoint needs a per-endpoint override via `[RequestSizeLimit]`.

```bash
# Check your configured limit
cat appsettings.json | jq '.RequestLimitsOptions.MaxRequestBodySize'

# Check actual request size
curl -X POST https://api.example.com/api/resource \
  -H "Content-Type: application/json" \
  -d @large-payload.json -v 2>&1 | grep "< HTTP"
```

### Form data rejected despite small body

The form has too many values. Check `MaxFormValueCount`. A form with 2000 fields exceeds the default limit of 1024.

### Startup validation failure

```text
Microsoft.Extensions.Options.OptionsValidationException:
  DataAnnotation validation failed for 'RequestLimitsOptions' members:
    'MaxRequestBodySize' with the error: 'The field MaxRequestBodySize must be between 1 and 9223372036854775807.'
```

`MaxRequestBodySize` must be >= 1. A value of 0 is not allowed.

### Inconsistency between Kestrel and form limits

This feature ensures both are set from the same source. If you see different behavior for JSON vs form-data requests, verify you're using `AddRequestLimits()` (or `AddApiPipeline()`) and not manually configuring `FormOptions` separately.

## References

- [Configure options for the ASP.NET Core Kestrel web server](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/servers/kestrel/options) — `KestrelServerOptions.Limits` including `MaxRequestBodySize`, `MaxRequestHeadersTotalSize`, and `MaxRequestHeaderCount`
- [Handle file uploads in ASP.NET Core](https://learn.microsoft.com/en-us/aspnet/core/mvc/models/file-uploads) — `FormOptions` limits and per-endpoint `[RequestSizeLimit]` overrides

## Related

- [OPERATIONS.md](../OPERATIONS.md) — production config baselines
- [forwarded-headers.md](forwarded-headers.md) — correct client IP for logging rejected requests
