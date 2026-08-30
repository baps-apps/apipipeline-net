# Output Caching

ASP.NET Core's server-side cache, after authentication and authorization. Nothing is cached until an
endpoint opts in. The store is in memory and per pod unless your app registers a distributed
`IOutputCacheStore` (for example Redis). Can't be enabled together with response caching.

## Setup

```json
{ "ApiPipeline": { "OutputCaching": { "Enabled": true } } }
```

```csharp
// Optional shared store: builder.Services.AddStackExchangeRedisOutputCache(o => o.Configuration = "redis:6379");

app.UseApiPipeline(pipeline => pipeline
    .WithAuthentication()
    .WithAuthorization()
    .WithOutputCaching());

app.MapGet("/api/products", GetProducts)
   .CacheOutput(p => p.Expire(TimeSpan.FromMinutes(5)).Tag("products"));

app.MapPost("/api/products", async (Product product, IOutputCacheStore cache, CancellationToken ct) =>
{
    // save...
    await cache.EvictByTagAsync("products", ct);
    return Results.Created($"/api/products/{product.Id}", product);
});
```

The framework caches only `GET`/`HEAD` 200 responses without cookies, and skips authenticated
requests unless a policy varies by user. Base and named policies go in your own
`AddOutputCache(o => ...)`.

## Options (section `ApiPipeline:OutputCaching`)

| Property | Default | Notes |
| --- | --- | --- |
| `Enabled` | `false` | |
| `DefaultExpiration` | `00:01:00` | When a policy sets none; 1 s–1 day |
| `SizeLimitBytes` | 100 MB | When full, new responses aren't cached |
| `MaximumBodySizeBytes` | 64 MB | Larger responses are served, not stored |
| `UseCaseSensitivePaths` | `false` | |

These apply only while enabled and only as defaults: your own `AddOutputCache(...)` settings win
([details](../ARCHITECTURE.md#framework-options-your-settings-win)). Moving from response caching:
see [`APIPIPELINE001`](../diagnostics.md#apipipeline001).

## Troubleshooting

| Symptom | Check |
| --- | --- |
| Nothing cached | `Enabled`, `WithOutputCaching()`, `.CacheOutput()` on the endpoint, unauthenticated GET, 200, no cookie |
| Eviction works on one pod only | In-memory store; register a distributed one |
| Stale data | Writes must evict the tag reads use |

## References

- [Output caching middleware in ASP.NET Core](https://learn.microsoft.com/en-us/aspnet/core/performance/caching/output)
