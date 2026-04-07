# Rate Limiting

## What it does

ApiPipeline.NET wraps ASP.NET Core's `System.Threading.RateLimiting` into a configuration-driven, multi-policy rate limiter. It partitions requests by authenticated user identity, then by client IP, with configurable behavior for anonymous traffic. Rejected requests receive RFC 7807 `ProblemDetails` responses with `Retry-After` headers.

## Why it matters

- **Protects backend services** from traffic spikes, abuse, and accidental client-side retry storms.
- **Per-user fairness**: authenticated users get individual rate-limit buckets, preventing one user from starving others.
- **Fail-fast startup validation**: misconfigured policies (missing `DefaultPolicy`, no policies defined) crash the app at startup rather than silently running unprotected.
- **Named policies**: different endpoints can have different rate limits (e.g., stricter on write endpoints, permissive on reads).

## How it works

```text
Request → Identity resolved? → Partition by user ID
                             → API key header? → Partition by API key (when enabled)
                             → No identity? → Partition by RemoteIpAddress
                             → No IP? → AnonymousFallback (Reject / RateLimit / Allow)
```

### Partition key priority

1. Authenticated user claim: `sub` → `nameid` → `ClaimTypes.NameIdentifier`
2. API key header (`X-Api-Key` by default, when `EnableApiKeyPartitioning` is `true`)
3. `RemoteIpAddress` (use forwarded headers behind proxy)
4. `AnonymousFallback` behavior

### Informational headers

When `EmitRateLimitHeaders` is `true` (default), every response includes:

| Header | Value | Purpose |
|---|---|---|
| `X-RateLimit-Limit` | Policy `PermitLimit` | Maximum requests allowed in the current window |
| `X-RateLimit-Reset` | Policy `WindowSeconds` | Window duration in seconds |

These headers appear on both successful (200) and rejected (429) responses, enabling client-side adaptive backoff.

### Rejection response

```json
{
  "type": "https://tools.ietf.org/html/rfc6585#section-4",
  "title": "Too Many Requests",
  "status": 429,
  "detail": "Rate limit exceeded. Retry after the duration indicated by the Retry-After header.",
  "correlationId": "abc-123",
  "traceId": "00-..."
}
```

Headers: `Retry-After: <seconds>`, `Cache-Control: no-store`, `X-RateLimit-Limit: <permit-limit>`, `X-RateLimit-Reset: <window-seconds>`.

## Registration

```csharp
// Aggregate (recommended)
builder.Services.AddApiPipeline(builder.Configuration);

// Or standalone
builder.Services.AddRateLimiting(builder.Configuration);
```

### Pipeline

```csharp
app.UseApiPipeline(pipeline => pipeline
    .WithForwardedHeaders()  // resolve real IP first
    .WithAuthentication()    // resolve identity first
    .WithAuthorization()
    .WithRateLimiting()      // rate limit after identity
    // ...
);
```

### Named policies on endpoints

```csharp
app.MapPost("/api/orders", handler).RequireRateLimiting("permissive");
app.MapGet("/api/search", handler).RequireRateLimiting("strict");
```

## All available options

### `RateLimitingOptions` (JSON section: `RateLimitingOptions`)

| Property | Type | Default | Description |
|---|---|---|---|
| `Enabled` | `bool` | `false` | Master switch. When `false`, no rate limiting is applied. |
| `DefaultPolicy` | `string` | `"strict"` | Name of the policy applied globally. Must match a configured policy name. |
| `Policies` | `List<RateLimitPolicy>` | `[]` | Collection of named rate limit policies. At least one required when enabled. |
| `AnonymousFallback` | `AnonymousFallbackBehavior` | `Reject` | Behavior when no user identity or IP is available. |
| `EmitRateLimitHeaders` | `bool` | `true` | When `true`, adds `X-RateLimit-Limit` and `X-RateLimit-Reset` headers to all responses. |
| `EnableApiKeyPartitioning` | `bool` | `false` | When `true`, requests with the `ApiKeyHeader` header are partitioned by API key for per-client rate limiting. |
| `ApiKeyHeader` | `string` | `"X-Api-Key"` | Header name for API key partitioning. Only used when `EnableApiKeyPartitioning` is `true`. |

### `AnonymousFallbackBehavior` enum

| Value | Behavior | Risk |
|---|---|---|
| `Reject` | Returns 429 immediately | Safe default — no shared bucket |
| `RateLimit` | Uses a single shared "anonymous" bucket | One client can exhaust for all anonymous traffic |
| `Allow` | Skips rate limiting entirely | Only safe with upstream enforcement |

### `RateLimitPolicy`

| Property | Type | Default | Required for | Description |
|---|---|---|---|---|
| `Name` | `string` | `""` | All | Unique policy name. Referenced by `DefaultPolicy` or `RequireRateLimiting()`. |
| `Kind` | `RateLimiterKind` | `FixedWindow` | All | Algorithm type. |
| `PermitLimit` | `int` | — | All | Max permits per window (or max token capacity for TokenBucket). Must be ≥ 1. |
| `WindowSeconds` | `int?` | `null` | FixedWindow, SlidingWindow, TokenBucket | Duration of the rate-limit window in seconds. |
| `QueueLimit` | `int` | `0` | All | Max queued requests waiting for permits. `0` = no queuing. |
| `QueueProcessingOrder` | `QueueProcessingOrder` | `OldestFirst` | All | `OldestFirst` or `NewestFirst`. |
| `AutoReplenishment` | `bool` | `true` | FixedWindow, TokenBucket | Whether permits auto-replenish when the window expires. |
| `SegmentsPerWindow` | `int?` | `null` | SlidingWindow | Subdivisions within the sliding window. More segments = smoother distribution. |
| `TokensPerPeriod` | `int?` | `null` | TokenBucket | Tokens added per `WindowSeconds` replenishment cycle. |

### `RateLimiterKind` enum

| Kind | Best for | Required fields |
|---|---|---|
| `FixedWindow` | Simple rate caps (e.g., 100 req/min) | `PermitLimit`, `WindowSeconds` |
| `SlidingWindow` | Smoother distribution than fixed windows | `PermitLimit`, `WindowSeconds`, `SegmentsPerWindow` |
| `TokenBucket` | Burst-tolerant APIs with sustained rate | `PermitLimit`, `WindowSeconds`, `TokensPerPeriod` |
| `Concurrency` | Limiting parallel in-flight requests | `PermitLimit` |

## Configuration examples

### FixedWindow (simplest)

```json
{
  "RateLimitingOptions": {
    "Enabled": true,
    "AnonymousFallback": "Reject",
    "DefaultPolicy": "standard",
    "Policies": [
      {
        "Name": "standard",
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

### SlidingWindow (smoother)

```json
{
  "Name": "smooth",
  "Kind": "SlidingWindow",
  "PermitLimit": 100,
  "WindowSeconds": 60,
  "SegmentsPerWindow": 6,
  "QueueLimit": 0,
  "QueueProcessingOrder": "OldestFirst"
}
```

The 60-second window is divided into 6 segments of 10 seconds each. Permits from expired segments are reclaimed gradually instead of all at once.

### TokenBucket (burst-tolerant)

```json
{
  "Name": "burst-tolerant",
  "Kind": "TokenBucket",
  "PermitLimit": 50,
  "WindowSeconds": 30,
  "TokensPerPeriod": 10,
  "QueueLimit": 5,
  "QueueProcessingOrder": "OldestFirst",
  "AutoReplenishment": true
}
```

Starts with 50 tokens. Every 30 seconds, 10 tokens are added (up to the 50 cap). Clients can burst up to 50 requests then sustain ~20 req/min.

### Concurrency (parallel limit)

```json
{
  "Name": "upload-limiter",
  "Kind": "Concurrency",
  "PermitLimit": 5,
  "QueueLimit": 10,
  "QueueProcessingOrder": "OldestFirst"
}
```

Only 5 concurrent requests allowed. Up to 10 additional requests queued.

### Multiple policies

```json
{
  "RateLimitingOptions": {
    "Enabled": true,
    "AnonymousFallback": "Reject",
    "DefaultPolicy": "standard",
    "Policies": [
      {
        "Name": "standard",
        "Kind": "FixedWindow",
        "PermitLimit": 100,
        "WindowSeconds": 60,
        "QueueLimit": 0
      },
      {
        "Name": "strict-writes",
        "Kind": "FixedWindow",
        "PermitLimit": 20,
        "WindowSeconds": 60,
        "QueueLimit": 0
      },
      {
        "Name": "permissive-reads",
        "Kind": "TokenBucket",
        "PermitLimit": 500,
        "WindowSeconds": 60,
        "TokensPerPeriod": 100,
        "QueueLimit": 0
      }
    ]
  }
}
```

```csharp
app.MapPost("/api/orders", handler).RequireRateLimiting("strict-writes");
app.MapGet("/api/products", handler).RequireRateLimiting("permissive-reads");
// All other endpoints use "standard" (the DefaultPolicy)
```

### API key partitioning (machine-to-machine)

```json
{
  "RateLimitingOptions": {
    "Enabled": true,
    "EnableApiKeyPartitioning": true,
    "ApiKeyHeader": "X-Api-Key",
    "AnonymousFallback": "Reject",
    "DefaultPolicy": "standard",
    "Policies": [
      {
        "Name": "standard",
        "Kind": "FixedWindow",
        "PermitLimit": 100,
        "WindowSeconds": 60,
        "QueueLimit": 0
      }
    ]
  }
}
```

Each API key gets its own rate limit bucket. Clients include `X-Api-Key: <their-key>` in requests. This supports per-client SLAs for service accounts and integrations without requiring full authentication infrastructure.

### Disabling informational headers

```json
{
  "RateLimitingOptions": {
    "Enabled": true,
    "EmitRateLimitHeaders": false
  }
}
```

Set `EmitRateLimitHeaders: false` if you prefer not to expose rate limit metadata to clients.

## Production recommendations

| Recommendation | Why |
|---|---|
| Set `AnonymousFallback` to `Reject` | Prevents shared-bucket exhaustion from unidentifiable clients |
| Configure forwarded headers **before** rate limiting | Without correct IP, all traffic behind proxy shares one bucket |
| Use named policies for sensitive endpoints | Write endpoints and auth endpoints should have stricter limits |
| Start conservative, loosen gradually | Begin with tight limits and monitor 429 rates before relaxing |
| Set `QueueLimit` to `0` for most APIs | Queuing masks latency issues — fail fast is usually better |
| Monitor `apipipeline.ratelimit.rejected` metric | Track rejection rate and partition distribution |
| Enable `EmitRateLimitHeaders` | Helps clients implement adaptive backoff based on `X-RateLimit-Limit` and `X-RateLimit-Reset` |
| Use API key partitioning for M2M traffic | Provides per-client SLAs without requiring full auth infrastructure |

## Non-production recommendations

| Recommendation | Why |
|---|---|
| Keep rate limiting enabled but with higher `PermitLimit` | Catches integration-test bugs where clients don't handle 429s |
| Use a single permissive FixedWindow policy | Avoids friction during development |
| Test 429 responses explicitly | Ensure your client code handles `Retry-After` headers |

## Startup validation

Rate limiting validates at startup. These errors crash the app intentionally:

| Error | Cause | Fix |
|---|---|---|
| `DefaultPolicy is required when rate limiting is enabled` | `DefaultPolicy` is empty | Set a policy name |
| `At least one policy must be configured` | `Policies` array is empty with `Enabled: true` | Add at least one policy |
| `DefaultPolicy 'X' does not match any configured policy name` | Typo or missing policy | Ensure names match (case-insensitive) |
| `Duplicate policy name 'X'` | Two policies with the same name | Use unique names |
| `WindowSeconds is required for FixedWindow policies` | Missing `WindowSeconds` | Add `WindowSeconds` to the policy |
| `SegmentsPerWindow is required for SlidingWindow policies` | Missing `SegmentsPerWindow` | Add `SegmentsPerWindow` |
| `TokensPerPeriod is required for TokenBucket policies` | Missing `TokensPerPeriod` | Add `TokensPerPeriod` |

## Troubleshooting

### Rate limiting has no effect

1. Verify `RateLimitingOptions:Enabled` is `true`.
2. Verify `WithRateLimiting()` is called in `UseApiPipeline`.
3. Verify `DefaultPolicy` matches a configured policy `Name`.
4. Check that forwarded headers run first when behind a proxy.

### All users share one rate-limit bucket

This means all requests have the same partition key (the proxy IP).

1. Enable forwarded headers: `WithForwardedHeaders()`.
2. Set `ClearDefaultProxies: true` and configure `KnownNetworks`.
3. Verify `X-Forwarded-For` is being forwarded by your ingress.

### Excessive 429s for legitimate users

1. Check `PermitLimit` and `WindowSeconds` against actual traffic volume.
2. Check if `AnonymousFallback` is `Reject` and many users lack identity.
3. Increase `QueueLimit` to absorb short bursts.
4. Consider switching to `TokenBucket` for burst tolerance.

### 429 responses missing `Retry-After`

`Retry-After` is only present when the underlying limiter provides `MetadataName.RetryAfter`. `FixedWindow` and `SlidingWindow` provide it; `Concurrency` does not.

## References

- [Rate limiting middleware in ASP.NET Core](https://learn.microsoft.com/en-us/aspnet/core/performance/rate-limit) — Microsoft's rate limiting middleware documentation
- [System.Threading.RateLimiting namespace](https://learn.microsoft.com/en-us/dotnet/api/system.threading.ratelimiting) — API reference for `FixedWindowRateLimiter`, `SlidingWindowRateLimiter`, `TokenBucketRateLimiter`, and `ConcurrencyLimiter`
- [RFC 6585 §4: 429 Too Many Requests](https://www.rfc-editor.org/rfc/rfc6585#section-4) — HTTP status code specification for rate-limited responses
- [Announcing rate limiting for .NET (blog)](https://devblogs.microsoft.com/dotnet/announcing-rate-limiting-for-dotnet/) — design rationale and algorithm comparison

## Related

- [OPERATIONS.md](../OPERATIONS.md) — production baseline config
- [RUNBOOK.md](../RUNBOOK.md) — incident response for 429 spikes
- [forwarded-headers.md](forwarded-headers.md) — required for correct IP partitioning behind proxy
