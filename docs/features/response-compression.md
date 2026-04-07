# Response Compression

## What it does

ApiPipeline.NET configures ASP.NET Core response compression with Brotli and Gzip providers, MIME type filtering, and path exclusions. Responses matching the configured criteria are compressed before transmission, reducing bandwidth and improving client-perceived latency.

## Why it matters

- **Bandwidth reduction**: JSON API responses compress well (typically 60–80% reduction).
- **Faster perceived latency**: smaller payloads transfer faster, especially on mobile networks.
- **Brotli + Gzip**: Brotli offers ~15–25% better compression than Gzip for text content; both are supported for broad client compatibility.
- **Path exclusions**: health endpoints and other lightweight responses skip compression overhead.
- **BREACH-aware defaults**: HTTPS compression is opt-in because enabling it can expose CRIME/BREACH side-channel vulnerabilities when responses mix secrets with attacker-controlled content.

## How it works

```text
Request (Accept-Encoding: br, gzip)
  → Path excluded? → skip compression
  → MIME type eligible? → compress with best available provider
  → Response sent compressed with Content-Encoding header
```

The middleware uses `CompressionLevel.Fastest` for both Brotli and Gzip by default, optimizing for latency over compression ratio. This is the correct tradeoff for APIs where response time matters more than a few extra bytes.

## Registration

```csharp
// Aggregate (recommended)
builder.Services.AddApiPipeline(builder.Configuration);

// Or standalone
builder.Services.AddResponseCompression(builder.Configuration);
```

### Pipeline

```csharp
app.UseApiPipeline(pipeline => pipeline
    // ...
    .WithResponseCompression()
    .WithResponseCaching()   // caching after compression stores the compressed form
    // ...
);
```

## All available options

### `ResponseCompressionSettings` (JSON section: `ResponseCompressionOptions`)

| Property | Type | Default | Description |
|---|---|---|---|
| `Enabled` | `bool` | `false` | Master switch. When `false`, no compression is applied. |
| `EnableForHttps` | `bool` | `false` | Whether to compress HTTPS responses. **Opt-in** due to BREACH risk. |
| `EnableBrotli` | `bool` | `true` | Register the Brotli compression provider. |
| `EnableGzip` | `bool` | `true` | Register the Gzip compression provider. |
| `MimeTypes` | `string[]?` | `null` | Whitelist of MIME types eligible for compression. When `null`, defaults to ASP.NET Core defaults + `application/json` + `application/problem+json`. |
| `ExcludedMimeTypes` | `string[]?` | `null` | MIME types that should never be compressed (e.g., already-compressed formats). |
| `ExcludedPaths` | `string[]?` | `["/health"]` | URL paths to skip compression on. Matched using `StartsWithSegments`. |

## Configuration examples

### Minimal (development)

```json
{
  "ResponseCompressionOptions": {
    "Enabled": true,
    "EnableForHttps": true,
    "EnableBrotli": true,
    "EnableGzip": true,
    "ExcludedPaths": ["/health"]
  }
}
```

### Production with explicit MIME types

```json
{
  "ResponseCompressionOptions": {
    "Enabled": true,
    "EnableForHttps": true,
    "EnableBrotli": true,
    "EnableGzip": true,
    "MimeTypes": [
      "application/json",
      "application/problem+json",
      "text/plain"
    ],
    "ExcludedMimeTypes": [
      "application/octet-stream",
      "image/png"
    ],
    "ExcludedPaths": [
      "/health",
      "/metrics"
    ]
  }
}
```

### Gzip only (legacy client compatibility)

```json
{
  "ResponseCompressionOptions": {
    "Enabled": true,
    "EnableForHttps": true,
    "EnableBrotli": false,
    "EnableGzip": true,
    "ExcludedPaths": ["/health"]
  }
}
```

## Production recommendations

| Recommendation | Why |
|---|---|
| Set `EnableForHttps: true` only if your API does not mix secrets with user-controlled data in response bodies | Mitigates BREACH/CRIME attacks. Most JSON APIs are safe. |
| Explicitly list `MimeTypes` | Prevents accidental compression of binary or pre-compressed content |
| Exclude health and metrics paths | These are high-frequency, small-payload — compression overhead isn't worth it |
| Use both Brotli and Gzip | Brotli gives better ratios; Gzip covers older clients |
| Compression level is `Fastest` | Correct for APIs — minimal latency impact with good compression |

## Non-production recommendations

| Recommendation | Why |
|---|---|
| Enable compression to test client compatibility | Catch decompression bugs early |
| Set `EnableForHttps: true` | Development traffic doesn't carry real secrets |
| Keep same `ExcludedPaths` as production | Ensures health checks work consistently |

## Troubleshooting

### Responses not compressed

1. Verify `ResponseCompressionOptions:Enabled` is `true`.
2. Verify `WithResponseCompression()` is called in `UseApiPipeline`.
3. Check that the client sends `Accept-Encoding: br, gzip` header.
4. Check that `EnableForHttps` is `true` if your API uses HTTPS.
5. Verify the response MIME type is in the `MimeTypes` list (or default list).
6. Check that the request path is not in `ExcludedPaths`.

```bash
# Test compression
curl -s -D - -H "Accept-Encoding: br, gzip" https://api.example.com/api/resource | grep -i "content-encoding"
# Expected: content-encoding: br  (or gzip)
```

### Health endpoint responses are compressed

The `/health` path is excluded by default. If you use a different health path, add it to `ExcludedPaths`:

```json
{
  "ExcludedPaths": ["/health", "/healthz", "/_health"]
}
```

### BREACH/CRIME security concern

If your API returns secrets (tokens, keys) in response bodies alongside user-controlled data, set `EnableForHttps: false`. Most REST APIs that return business data (not secrets) are safe.

## References

- [Response compression in ASP.NET Core](https://learn.microsoft.com/en-us/aspnet/core/performance/response-compression) — Microsoft's response compression middleware documentation, including BREACH/CRIME security considerations
- [MDN: Content-Encoding](https://developer.mozilla.org/en-US/docs/Web/HTTP/Headers/Content-Encoding) — how `br` and `gzip` content encodings work
- [Brotli compression format (RFC 7932)](https://www.rfc-editor.org/rfc/rfc7932) — the Brotli compressed data format specification

## Related

- [response-caching.md](response-caching.md) — caching works with compression (stores compressed form)
- [OPERATIONS.md](../OPERATIONS.md) — production config baselines
