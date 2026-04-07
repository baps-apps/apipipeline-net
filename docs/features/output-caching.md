# Output Caching

## What it does

ApiPipeline.NET integrates ASP.NET Core Output Caching as the modern replacement for `ResponseCachingMiddleware`. Output Caching supports distributed backing stores (Redis), tag-based eviction, per-endpoint policies, and programmatic cache invalidation — features not available in the legacy response caching middleware.

## Why it matters

- **Distributed caching**: back the cache with Redis or any `IOutputCacheStore` implementation for multi-instance deployments.
- **Tag-based invalidation**: evict cached responses by tag (e.g., invalidate all product-related caches when a product changes) instead of waiting for expiry.
- **Per-endpoint policies**: attach caching policies directly to endpoints instead of relying solely on `Cache-Control` headers.
- **Migration path**: the existing `ResponseCachingSettings.PreferOutputCaching` flag signals when teams should migrate. Output Caching and Response Caching can coexist during the transition.
- **Phase-enforced ordering**: the pipeline builder places Output Caching after authentication and authorization, preventing the same auth-bypass risk that affects misconfigured response caching.

## How it works

```text
Request → Auth → Rate Limiting → Compression → Response Caching → Output Caching
                                                                    ↓
                                                              Cache hit? → Return cached response
                                                              Cache miss? → Execute endpoint → Cache response
```

Output Caching evaluates per-endpoint policies (set via `.CacheOutput()`) and stores/retrieves responses independently of `Cache-Control` headers. This gives server-side control over what gets cached, for how long, and how it is invalidated.

### Output Caching vs Response Caching

| Feature | Response Caching (core) | Output Caching (core) |
|---|---|---|
| Storage | In-memory only | In-memory, Redis, custom stores |
| Invalidation | No programmatic invalidation | Tag-based invalidation |
| Policies | `Cache-Control` header driven | Per-endpoint policies via `.CacheOutput()` |
| Vary support | `Vary` header | Policies + `Vary` |
| Distributed | No | Yes (Redis-backed via `IOutputCacheStore`) |
| .NET version | All | .NET 7+ |

## Registration

```csharp
// Aggregate (recommended) — opt-in via registration options
builder.Services.AddApiPipeline(builder.Configuration, options =>
{
    options.AddOutputCaching = true;
});

// Or standalone
builder.Services.AddOutputCaching(builder.Configuration);
```

### Pipeline

```csharp
app.UseApiPipeline(pipeline => pipeline
    // ...
    .WithAuthentication()
    .WithAuthorization()
    .WithResponseCompression()
    .WithOutputCaching()       // after auth to avoid caching unauthorized responses
    .WithSecurityHeaders()
    // ...
);
```

Output Caching is positioned after authentication and authorization in the pipeline to prevent serving cached authenticated responses to unauthenticated users. You can use it alongside or instead of `WithResponseCaching()`.

### Per-endpoint caching policies

```csharp
app.MapGet("/api/products", async () =>
{
    // expensive query...
    return Results.Ok(products);
}).CacheOutput(policy => policy
    .Expire(TimeSpan.FromMinutes(5))
    .Tag("products"));

app.MapGet("/api/products/{id}", async (int id) =>
{
    return Results.Ok(product);
}).CacheOutput(policy => policy
    .Expire(TimeSpan.FromMinutes(10))
    .Tag("products", $"product-{id}"));
```

### Programmatic invalidation

```csharp
app.MapPost("/api/products", async (
    Product product,
    IOutputCacheStore cache,
    CancellationToken ct) =>
{
    // save product...
    await cache.EvictByTagAsync("products", ct);
    return Results.Created($"/api/products/{product.Id}", product);
});
```

## All available options

### `OutputCachingSettings` (JSON section: `OutputCachingOptions`)

| Property | Type | Default | Description |
|---|---|---|---|
| `Enabled` | `bool` | `false` | Master switch. When `false`, output caching middleware is not added. Opt-in since this is a migration target. |

Advanced `OutputCacheOptions` configuration (policies, base path, size limits) can be set via the standard ASP.NET Core `AddOutputCache(options => { ... })` API if you need more control beyond the feature flag.

## Configuration examples

### Enable output caching

```json
{
  "OutputCachingOptions": {
    "Enabled": true
  }
}
```

### Coexist with response caching during migration

```json
{
  "ResponseCachingOptions": {
    "Enabled": true,
    "SizeLimitBytes": 52428800,
    "PreferOutputCaching": true
  },
  "OutputCachingOptions": {
    "Enabled": true
  }
}
```

Setting `PreferOutputCaching: true` on `ResponseCachingSettings` is a documentation signal to your team that the service should migrate to output caching. Both can run simultaneously — response caching handles `Cache-Control`-driven caching while output caching handles `.CacheOutput()` policies.

### Disable output caching (default)

```json
{
  "OutputCachingOptions": {
    "Enabled": false
  }
}
```

## Production recommendations

| Recommendation | Why |
|---|---|
| Place output caching **after** auth/authorization in the pipeline | Prevents serving cached authenticated content to anonymous users (enforced automatically by `UseApiPipeline`) |
| Use Redis-backed `IOutputCacheStore` in multi-instance deployments | In-memory cache is per-instance; only Redis provides shared cache across pods |
| Use tag-based invalidation for mutable resources | Ensures stale data is evicted when the underlying data changes |
| Set explicit `Expire` durations on each endpoint | Prevents unbounded cache growth |
| Exclude health and monitoring endpoints from caching | Health probes should always hit the live application |
| Migrate from `WithResponseCaching()` to `WithOutputCaching()` incrementally | Both can coexist — move endpoints one at a time |

## Non-production recommendations

| Recommendation | Why |
|---|---|
| Enable output caching in staging to test policies | Ensures cache behavior matches production before deployment |
| Use short `Expire` durations in development | Prevents stale data during active development |
| Test tag-based invalidation flows | Verify that writes correctly evict related cached responses |

## Troubleshooting

### Responses not being cached

1. Verify `OutputCachingOptions:Enabled` is `true`.
2. Verify `WithOutputCaching()` is called in `UseApiPipeline`.
3. Check that your endpoint uses `.CacheOutput()` — output caching requires explicit opt-in per endpoint.
4. Verify the response status code is 200 (only successful responses are cached by default).

### Cached responses served to wrong users

This indicates caching is positioned before authorization in the pipeline. `UseApiPipeline` enforces the correct order automatically — verify you're using `WithOutputCaching()` inside the builder, not calling `app.UseOutputCache()` manually before auth.

### Stale data after updates

1. Verify your write endpoints call `IOutputCacheStore.EvictByTagAsync()` to invalidate related tags.
2. Check that the correct tags are assigned to both read and write endpoints.
3. For Redis-backed stores, verify the Redis connection is healthy.

### Cache not shared across instances

In-memory output caching is per-process. For multi-instance deployments:
1. Register a Redis-backed `IOutputCacheStore`.
2. Verify all instances connect to the same Redis instance/cluster.

## References

- [Output caching middleware in ASP.NET Core](https://learn.microsoft.com/en-us/aspnet/core/performance/caching/output) — Microsoft's output caching middleware documentation
- [IOutputCacheStore interface](https://learn.microsoft.com/en-us/dotnet/api/microsoft.aspnetcore.outputcaching.ioutputcachestore) — custom store implementation
- [Response Caching Middleware in ASP.NET Core](https://learn.microsoft.com/en-us/aspnet/core/performance/caching/middleware) — legacy response caching (for comparison)

## Related

- [response-caching.md](response-caching.md) — legacy response caching (migration source)
- [response-compression.md](response-compression.md) — compression runs before caching
- [OPERATIONS.md](../OPERATIONS.md) — production config baselines
- [ARCHITECTURE.md](../ARCHITECTURE.md) — middleware ordering rationale
