# Response Caching

## What it does

ApiPipeline.NET registers ASP.NET Core's in-memory response caching middleware, which serves previously computed responses for cacheable requests without re-executing the endpoint. Configuration controls cache size and path case sensitivity. When CORS is also enabled, `Vary: Origin` is automatically appended to prevent cross-origin cache poisoning.

## Why it matters

- **Reduces server load**: identical requests are served from cache instead of hitting your endpoint logic.
- **Lower latency**: cached responses bypass all downstream processing.
- **Simple activation**: controlled entirely through configuration with no code changes to endpoints.
- **Migration path**: the `PreferOutputCaching` flag signals when teams should migrate to the core package's built-in output caching (`WithOutputCaching()`).

## How it works

ASP.NET Core `ResponseCachingMiddleware` caches responses based on `Cache-Control` and `Vary` headers set by your endpoint. The middleware intercepts requests and, if a matching cached response exists, returns it without invoking downstream middleware.

**Important**: Response caching is positioned **after** authentication and authorization in the pipeline to prevent serving cached authenticated responses to unauthenticated users.

**Vary: Origin**: When both CORS and response caching are enabled, `UseResponseCaching()` automatically injects a `Vary: Origin` header on all responses. This ensures that a response cached for origin A is not incorrectly served to origin B with wrong CORS headers. This is handled transparently — no additional configuration is needed.

```text
Request → Auth → Rate Limiting → Compression → Response Caching
                                                ↓
                                          Cache hit? → Return cached response
                                          Cache miss? → Execute endpoint → Cache response
```

### What gets cached

Only responses with appropriate `Cache-Control` headers are cached. You must set these in your endpoints:

```csharp
app.MapGet("/api/products", () =>
{
    // ...
}).CacheOutput(policy => policy.Expire(TimeSpan.FromMinutes(5)));

// Or via response headers in the handler
context.Response.Headers.CacheControl = "public, max-age=300";
```

## Registration

```csharp
// Aggregate (recommended)
builder.Services.AddApiPipeline(builder.Configuration);

// Or standalone
builder.Services.AddResponseCaching(builder.Configuration);
```

### Pipeline

```csharp
app.UseApiPipeline(pipeline => pipeline
    // ...
    .WithAuthentication()
    .WithAuthorization()
    .WithResponseCompression()   // compress before caching (stores compressed form)
    .WithResponseCaching()       // after auth to avoid caching unauthorized responses
    // ...
);
```

## All available options

### `ResponseCachingSettings` (JSON section: `ResponseCachingOptions`)

| Property | Type | Default | Description |
|---|---|---|---|
| `Enabled` | `bool` | `false` | Master switch. When `false`, response caching middleware is not added. |
| `SizeLimitBytes` | `long?` | `null` | Approximate in-memory cache size limit in bytes. When `null`, uses ASP.NET Core default (100 MB). |
| `UseCaseSensitivePaths` | `bool` | `false` | Whether cache lookups treat URL paths as case-sensitive. |
| `PreferOutputCaching` | `bool` | `false` | Migration signal. When `true`, consumers should enable `WithOutputCaching()` in the pipeline. Does not change behavior on its own. |

## Configuration examples

### Basic caching

```json
{
  "ResponseCachingOptions": {
    "Enabled": true,
    "SizeLimitBytes": 52428800,
    "UseCaseSensitivePaths": false
  }
}
```

50 MB cache, case-insensitive path matching.

### Large cache for read-heavy APIs

```json
{
  "ResponseCachingOptions": {
    "Enabled": true,
    "SizeLimitBytes": 209715200,
    "UseCaseSensitivePaths": false
  }
}
```

200 MB cache for APIs with many cacheable endpoints.

### Migration to Output Caching

```json
{
  "ResponseCachingOptions": {
    "Enabled": true,
    "SizeLimitBytes": 52428800,
    "UseCaseSensitivePaths": false,
    "PreferOutputCaching": true
  }
}
```

Setting `PreferOutputCaching: true` is a documentation signal — it communicates to your team that this service should migrate to output caching via `WithOutputCaching()` in the pipeline. See [output-caching.md](output-caching.md).

## Response Caching vs Output Caching

| Feature | Response Caching (core) | Output Caching (satellite) |
|---|---|---|
| Storage | In-memory only | In-memory, Redis, custom stores |
| Invalidation | No programmatic invalidation | Tag-based invalidation |
| Policies | `Cache-Control` header driven | Per-endpoint policies |
| Vary support | `Vary` header | Policies + `Vary` |
| Distributed | No | Yes (Redis-backed) |
| .NET version | All | .NET 7+ |

For new services or services that need distributed caching, prefer Output Caching — now available directly in the core package via `WithOutputCaching()`. See [output-caching.md](output-caching.md) for the full guide.

## Production recommendations

| Recommendation | Why |
|---|---|
| Place caching **after** auth/authorization in the pipeline | Prevents serving cached authenticated content to anonymous users |
| Set `SizeLimitBytes` based on your memory budget | Default 100 MB can be excessive in memory-constrained containers |
| `Vary: Origin` is added automatically when CORS + caching are both active | Prevents cross-origin cache pollution (handled by ApiPipeline.NET) |
| Only cache GET requests with explicit cache headers | POST/PUT/DELETE should never be cached |
| Exclude error responses from caching | ApiPipeline.NET already adds `Cache-Control: no-store` to all ProblemDetails |
| Consider `WithOutputCaching()` for new services | Better invalidation, distributed store, tag-based policies — see [output-caching.md](output-caching.md) |

## Non-production recommendations

| Recommendation | Why |
|---|---|
| Keep caching enabled to test behavior | Ensures your cache headers work correctly before production |
| Use a smaller `SizeLimitBytes` | Reduces memory footprint in dev environments |
| Test cache invalidation flows | Verify stale data doesn't persist across deployments |

## Troubleshooting

### Responses not being cached

1. Verify `ResponseCachingOptions:Enabled` is `true`.
2. Verify `WithResponseCaching()` is called in `UseApiPipeline`.
3. Check that your endpoint sets appropriate `Cache-Control` headers (e.g., `public, max-age=300`).
4. Verify the response is a GET request — POST/PUT/DELETE are not cached.
5. Check that `Authorization` header handling is correct (by default, responses with `Authorization` are not cached unless explicitly allowed).

### Cached responses served to wrong users

This indicates caching is positioned before authorization in the pipeline. `UseApiPipeline` enforces the correct order automatically — verify you're using `WithResponseCaching()` inside the builder, not calling `app.UseResponseCaching()` manually before auth.

### Stale data after deployment

In-memory response caching is cleared on app restart. If you see stale data:
1. Ensure the app was actually restarted (not just config-reloaded).
2. Check for upstream CDN or proxy caching.
3. Consider switching to Output Caching with programmatic invalidation.

## References

- [Response Caching Middleware in ASP.NET Core](https://learn.microsoft.com/en-us/aspnet/core/performance/caching/middleware) — Microsoft's response caching middleware documentation
- [Response caching in ASP.NET Core](https://learn.microsoft.com/en-us/aspnet/core/performance/caching/response) — `Cache-Control`, `Vary`, and HTTP caching semantics
- [Output caching middleware in ASP.NET Core](https://learn.microsoft.com/en-us/aspnet/core/performance/caching/output) — the newer Output Caching alternative (.NET 7+)

## Related

- [output-caching.md](output-caching.md) — modern replacement with distributed store and tag-based invalidation
- [response-compression.md](response-compression.md) — compression runs before caching
- [cors.md](cors.md) — CORS interaction with caching (Vary: Origin)
- [OPERATIONS.md](../OPERATIONS.md) — production config baselines
- [ARCHITECTURE.md](../ARCHITECTURE.md) — middleware ordering rationale
