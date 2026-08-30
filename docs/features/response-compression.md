# Response Compression

Brotli or Gzip at the `Fastest` level, through ASP.NET Core's middleware.

> **HTTPS responses are not compressed by default** (`EnableForHttps: false`, because of BREACH).
> Behind TLS-terminating ingress with forwarded headers, every request is HTTPS, so nothing is
> compressed until you set it (startup logs this). Or let the ingress compress.

## Setup

```json
{
  "ApiPipeline": {
    "ResponseCompression": { "Enabled": true, "EnableForHttps": true }
  }
}
```

```csharp
app.UseApiPipeline(pipeline => pipeline.WithResponseCompression());
```

## Options (section `ApiPipeline:ResponseCompression`)

| Property | Default | Notes |
| --- | --- | --- |
| `Enabled` | `false` | |
| `EnableForHttps` | `false` | Safe only if no response mixes secrets with reflected caller input |
| `EnableBrotli` | `true` | At least one codec must stay on |
| `EnableGzip` | `true` | |
| `MimeTypes` | framework defaults + JSON + problem JSON | A list replaces the default |
| `ExcludedMimeTypes` | none | |
| `ExcludedPaths` | `/health`, `/internal/health` | Matched by segment, ignoring case; a list, even `[]`, replaces the default |

Compression runs outside the caches: one uncompressed entry is cached and compressed per hit.

## Troubleshooting

| Symptom | Check |
| --- | --- |
| Nothing compressed | `Enabled`, `WithResponseCompression()`, `EnableForHttps` |
| One endpoint not compressed | Its content type, an excluded path, or no `Accept-Encoding` |
| `/health` compressed | Your `ExcludedPaths` replaced the default |

```bash
curl -sD - -o /dev/null -H "Accept-Encoding: br, gzip" https://api.example.com/api/resource | grep -i content-encoding
```

## References

- [Response compression in ASP.NET Core](https://learn.microsoft.com/en-us/aspnet/core/performance/response-compression)
