# Response Caching

> **Obsolete** ([`APIPIPELINE001`](../diagnostics.md#apipipeline001)); removed in the next major
> version. Use [output caching](output-caching.md). The two can't be enabled together.

ASP.NET Core's `ResponseCachingMiddleware`: in memory, per pod, following HTTP caching headers.

```json
{ "ApiPipeline": { "ResponseCaching": { "Enabled": true, "SizeLimitBytes": 52428800 } } }
```

```csharp
app.UseApiPipeline(pipeline => pipeline
    .WithAuthentication()
    .WithAuthorization()
    .WithResponseCaching());
```

A response is cached only for a `GET`/`HEAD` without `Authorization`, with status 200,
`Cache-Control: public, max-age=...` and no cookie. So authenticated APIs gain little, and clients can
bypass it with `Cache-Control: no-cache`. With CORS on, `Vary: Origin` is added.

```csharp
[ResponseCache(Duration = 300, Location = ResponseCacheLocation.Any)]
public IActionResult GetProducts() => Ok(products);
```

| Property | Default | Notes |
| --- | --- | --- |
| `Enabled` | `false` | Not with output caching |
| `SizeLimitBytes` | 100 MB | Per pod |
| `UseCaseSensitivePaths` | `false` | |

## Troubleshooting

| Symptom | Check |
| --- | --- |
| Nothing cached | `Cache-Control: public, max-age`; GET/HEAD without `Authorization` |
| Pods disagree | The cache is per pod |
| Stale data | Entries can't be evicted early; use output caching with tags |

## References

- [Response Caching Middleware](https://learn.microsoft.com/en-us/aspnet/core/performance/caching/middleware)
