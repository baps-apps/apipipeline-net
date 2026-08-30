# Rate Limiting

> **Limits are per pod.** ASP.NET Core's limiter is in-process: `PermitLimit: 100/min` on 10 pods
> admits up to 1,000/min, and a restart resets the pod's buckets. Enforce customer-facing limits at
> the gateway, or share them across pods with the [Redis add-on](#shared-limits-across-pods).

Named policies, one of them the default; a bucket per client (user, API key or IP); 429 with
`Retry-After` when a bucket is empty. An optional [edge limiter](#edge-rate-limiting) sheds IP floods
before authentication.

## Quick start

```json
{
  "ApiPipeline": {
    "RateLimiting": {
      "Enabled": true,
      "DefaultPolicy": "standard",
      "Policies": [
        { "Name": "standard", "Kind": "FixedWindow", "PermitLimit": 100, "Window": "00:01:00" }
      ]
    }
  }
}
```

```csharp
builder.Services.AddApiPipeline(builder.Configuration);

app.UseApiPipeline(pipeline => pipeline
    .WithForwardedHeaders()   // real client IP
    .WithAuthentication()     // users get their own bucket
    .WithAuthorization()
    .WithRateLimiting());
```

## Who shares a bucket

First match wins:

1. Your [resolvers](#custom-partitioning-tenant-or-client).
2. A `PartitionClaims` claim of the authenticated caller.
3. The authenticated user (`NameIdentifier`, `sub`, then `nameid`).
4. An [allow-listed API key](#api-key-partitioning).
5. The client IP; IPv6 grouped by `IPv6PartitionPrefixLength` (default `/64`).
6. No IP: `AnonymousFallback` decides.

| `AnonymousFallback` | Effect | Risk |
| --- | --- | --- |
| `Reject` (default) | 429, no `Retry-After` | Every anonymous request on a [socket listener](#unix-sockets-and-named-pipes) |
| `RateLimit` | One shared bucket | One client exhausts it for all |
| `Allow` | Not limited | Only with limiting upstream |

Behind a proxy without [forwarded headers](forwarded-headers.md), every client shares the proxy's
bucket.

### Unix sockets and named pipes

On a Unix socket or named pipe listener every request has a null `RemoteIpAddress`, and forwarded
headers are not applied (the framework would trust any caller). With `AnonymousFallback: Reject`,
every request without a user or allow-listed key gets 429, except `ExcludedPaths`. Startup logs a
warning with the address. Fix: listen on `http://127.0.0.1:8080` and set
`ForwardedHeaders:KnownProxies: ["127.0.0.1"]`, or accept another `AnonymousFallback`.

## Default, named and global policies

```csharp
app.MapGet("/reports", GetReports).RequireRateLimiting("reports"); // "reports" (+ GlobalPolicy)
app.MapGet("/orders", GetOrders);                                   // DefaultPolicy (+ GlobalPolicy)
app.MapGet("/status", Status).DisableRateLimiting();                // not limited
```

| Setting | Applies to |
| --- | --- |
| `DefaultPolicy` | Endpoints with no policy of their own. A fallback, not a cap: a named policy can be looser. |
| `GlobalPolicy` | Every limited request, on top of its own policy or the default (the framework's `GlobalLimiter`). |

Set `GlobalPolicy` equal to `DefaultPolicy` to get plain ASP.NET Core's behaviour: the default caps
every endpoint, spent once per request. A rejected request still spends global permits (two in .NET
10: one synchronous and one asynchronous attempt), so size the global ceiling generously. A 429
names the policy that rejected it.

### Health probes

`ExcludedPaths` (default `/health`, `/internal/health`) are not limited by either limiter: a 429 on
a probe restarts a healthy pod. A request is exempt only when all of these hold:

- it is a `GET` or `HEAD`, the methods HTTP probes send;
- its path is an excluded path or below one (`/health/ready` is; `/healthcare` is not);
- it carries no proxy header, so it didn't come through the ingress;
- it comes from `ProbeNetworks` (default: loopback and the private ranges);
- its endpoint asks for no policy of its own (`RequireRateLimiting`, `[EnableRateLimiting]`).

The default networks are wide because kubelet probes come from node addresses, which are private
in most clusters and which the package can't know. With the defaults, these callers also get
unlimited `GET`s under the probe paths:

- any pod or service inside the cluster;
- outside clients reaching the pods through a layer 4 load balancer that replaces their address
  with a private one and adds no proxy header: a Kubernetes `LoadBalancer` or `NodePort` Service
  with `externalTrafficPolicy: Cluster` (the default), or a cloud load balancer without client IP
  preservation.

A readiness check that queries a database or another service then lets those callers send it
unlimited load. Before production:

1. List your exact probe paths in `ExcludedPaths`, for both limiters, and put nothing else under
   them.
2. Set `ProbeNetworks` to the addresses your probes come from (the node network), as the pod sees
   them: a probe's `RemoteIpAddress` in the request log shows it.
3. Keep readiness checks cheap, or cache their result.

```json
"RateLimiting": {
  "ExcludedPaths": [ "/health/live", "/health/ready" ],
  "ProbeNetworks": [ "10.240.0.0/16" ]
},
"EdgeRateLimiting": {
  "ExcludedPaths": [ "/health/live", "/health/ready" ]
}
```

The edge limiter uses the same `ProbeNetworks`.

## Rejection response

Every 429, from either limiter:

- **`Retry-After`** in seconds when known. A sliding window reports one segment; concurrency and
  `AnonymousFallback: Reject` report none.
- **`Cache-Control: no-store`.**
- **`RateLimit-Policy` and `RateLimit`** if `EmitRateLimitHeadersOnRejection` is on.
- **An RFC 9457 body**, through your `IProblemDetailsService` when registered (see
  [error handling](../../README.md#error-handling)):

```json
{
  "type": "https://www.rfc-editor.org/rfc/rfc6585#section-4",
  "title": "Too Many Requests",
  "status": 429,
  "detail": "The rate limit was exceeded. Retry after 42 seconds."
}
```

The status stays 429 even if your own `AddRateLimiter` replaces `OnRejected`. The body starts the
response, so middleware adding headers to a 429 must use `Response.OnStarting`. CORS exposes the
rate-limit headers to browsers.

## Rate-limit headers

IETF [draft-ietf-httpapi-ratelimit-headers](https://datatracker.ietf.org/doc/draft-ietf-httpapi-ratelimit-headers/)
revision -11. A draft, not an RFC: the syntax may change; treat the fields as hints.

| Field | Example | Sent |
| --- | --- | --- |
| `RateLimit-Policy` | `"standard";q=100;w=60` | 429s with `EmitRateLimitHeadersOnRejection`; successes with `EmitRateLimitHeaders` |
| `RateLimit` | `"standard";r=0;t=60` | 429s with `EmitRateLimitHeadersOnRejection` |

A token bucket advertises its refill rate, not its burst; concurrency states
`qu="concurrent-requests"` without `w`. Both settings are off by default: on 429s the fields tell
an anonymous caller your quota, and on successes they cost every request.

## Options

### `ApiPipeline:RateLimiting`

| Property | Default | Notes |
| --- | --- | --- |
| `Enabled` | `false` | |
| `DefaultPolicy` | `"strict"` | Must name a policy |
| `GlobalPolicy` | none | Must name a policy |
| `Policies` | empty | At least one when enabled |
| `AnonymousFallback` | `Reject` | `Reject`, `RateLimit`, `Allow` |
| `EmitRateLimitHeaders` | `false` | `RateLimit-Policy` on successes |
| `EmitRateLimitHeadersOnRejection` | `false` | Fields on 429s, both limiters |
| `EnableApiKeyPartitioning` | `false` | Requires `KnownApiKeyHashes` |
| `ApiKeyHeader` | `X-Api-Key` | |
| `KnownApiKeyHashes` | none | 32 lowercase hex characters each |
| `PartitionClaims` | none | Claim types, first match wins, e.g. `["tenant_id"]` |
| `IPv6PartitionPrefixLength` | `64` | 1–128; also used by the edge limiter |
| `MaxIpPartitions` | `100000` | Address partitions kept; past it new addresses share one bucket per policy (event 24) |
| `MaxIdentityPartitions` | `100000` | Identity partitions kept; past it a new caller is limited by its address (event 25) |
| `ExcludedPaths` | `/health`, `/internal/health` | Matched by segment, ignoring case. Exempt only for `GET`/`HEAD`, without proxy headers, from `ProbeNetworks`, and without an explicit endpoint policy ([details](#health-probes)). A configured list, even `[]`, replaces the default. |
| `ProbeNetworks` | loopback and private ranges | CIDR ranges probes come from; narrow to your node network. Validated even when disabled. |

### `ApiPipelineRateLimitPolicy`

| Property | Default | Notes |
| --- | --- | --- |
| `Name` | | Required, unique |
| `Kind` | `FixedWindow` | `FixedWindow`, `SlidingWindow`, `TokenBucket`, `Concurrency` |
| `PermitLimit` | | ≥ 1; a token bucket's size |
| `Window` | | Whole seconds; required except for `Concurrency` |
| `SegmentsPerWindow` | | Required for `SlidingWindow`; 1–1000. With Redis at most 100; keep it at 10 or below ([cost](#sliding-window-cost)) |
| `TokensPerPeriod` | | Required for `TokenBucket`: tokens per `Window` |
| `QueueLimit` | `0` | Requests that may wait; `0` rejects at once |
| `QueueProcessingOrder` | `OldestFirst` | |

```json
{ "Name": "standard", "Kind": "FixedWindow", "PermitLimit": 100, "Window": "00:01:00" }
{ "Name": "smooth", "Kind": "SlidingWindow", "PermitLimit": 100, "Window": "00:01:00", "SegmentsPerWindow": 6 }
{ "Name": "bursty", "Kind": "TokenBucket", "PermitLimit": 50, "Window": "00:00:30", "TokensPerPeriod": 10 }
{ "Name": "uploads", "Kind": "Concurrency", "PermitLimit": 5, "QueueLimit": 10 }
```

## Custom partitioning (tenant or client)

For one bucket per tenant or OAuth client, name the claim: `"PartitionClaims": ["tenant_id"]` (or
`["azp", "client_id"]`). Claims come from the authenticated identity only; a caller without one falls
back to its user id.

For anything else, implement `IRateLimitPartitionKeyResolver`:

```csharp
public sealed class TenantPartitionKeyResolver : IRateLimitPartitionKeyResolver
{
    public RateLimitPartitionKey? Resolve(HttpContext context, ApiPipelineRateLimitPolicy policy) =>
        context.User.FindFirst("tenant_id")?.Value is { } tenant
            ? new RateLimitPartitionKey("tenant", tenant)
            : null;   // next resolver, then the built-in chain
}

builder.Services.AddApiPipelineRateLimitPartitionKeyResolver<TenantPartitionKeyResolver>();
```

- Resolvers run before `PartitionClaims`, in registration order.
- `Type` is a metric attribute: a fixed value from your code, `[a-z0-9_-]{1,32}`.
- Partition only on values the caller can't choose (validated claims); a raw header lets a caller
  mint a bucket per request.
- Synchronous, no I/O, thread-safe.
- A resolver that throws is skipped and logged at Error (event 23); alert on it.
- The edge limiter does not use resolvers.

## API key partitioning

```json
"EnableApiKeyPartitioning": true,
"KnownApiKeyHashes": [ "9f86d081884c7d659a2feaa0c55ad015" ]
```

Each listed key in `X-Api-Key` gets its own bucket; other keys are limited by IP. Hash a key with:

```csharp
Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(apiKey)).AsSpan(0, 16));
```

This is not authentication. The hash is unsalted, so use random keys of 128 bits or more
(`Convert.ToHexString(RandomNumberGenerator.GetBytes(16))`).

## Edge rate limiting

Sheds floods by IP before authentication, so they don't pay for token validation.

```json
"EdgeRateLimiting": { "Enabled": true, "PermitLimit": 600, "Window": "00:01:00" }
```

```csharp
app.UseApiPipeline(pipeline => pipeline
    .WithCors()
    .WithEdgeRateLimiting()
    .WithAuthentication()
    .WithAuthorization()
    .WithRateLimiting());
```

| Property | Default | Notes |
| --- | --- | --- |
| `Enabled` | `false` | |
| `PermitLimit` | `600` | Per IP per window; `Retry-After` is what's left of the window |
| `Window` | `00:01:00` | Whole seconds |
| `ExcludedPaths` | `/health`, `/internal/health` | As for the main limiter |
| `MaxIpPartitions` | `100000` | |

Set it generously: a NAT or carrier shares one IP. It reuses `IPv6PartitionPrefixLength`,
`ProbeNetworks` and `EmitRateLimitHeadersOnRejection`, reports policy `"edge"`, passes requests
without an IP to the main limiter, stays per pod with Redis, and shows only in
`apipipeline.edge_rate_limiting.rejected`.

## Shared limits across pods

`ApiPipeline.NET.RateLimiting.Redis` keeps every policy's state in Redis, so `PermitLimit: 100`
means 100 across all pods. All four kinds are supported. Requires Redis 5.0+ (tested on 5.0, 7.4 and
Cluster) and StackExchange.Redis 3.4.35 or later: an application that references an older version
itself fails restore with `NU1605` until it raises that reference
([troubleshooting](../TROUBLESHOOTING.md#restore-fails-with-nu1605-for-stackexchangeredis)). Without
`Enabled: true`, nothing changes.

```bash
dotnet add package ApiPipeline.NET.RateLimiting.Redis
```

```csharp
builder.Services.AddApiPipeline(builder.Configuration);
builder.Services.AddApiPipelineRedisRateLimiting(builder.Configuration);
// or (o => ...) or (configuration, sp => multiplexer)
```

```json
"RateLimiting": {
  "Redis": {
    "Enabled": true,
    "Configuration": "redis:6379",
    "KeyPrefix": "orders-api:prod",
    "PartitionKeySecret": "<from your secret store>"
  }
}
```

### Options (`ApiPipeline:RateLimiting:Redis`)

| Property | Default | Notes |
| --- | --- | --- |
| `Enabled` | `false` | |
| `Configuration` | none | Connection string; never logged; must parse |
| `KeyPrefix` | `{App}:{Env}` in Development | Required elsewhere; unique per API and environment; no whitespace or braces |
| `PartitionKeySecret` | plain SHA-256 in Development | Required elsewhere; ≥ 32 characters, same on every pod |
| `CommandTimeout` | `00:00:00.250` | 50 ms–10 s; most a request waits per call |
| `FallbackCooldown` | `00:00:30` | How long the open circuit skips Redis |
| `FailureThreshold` | `5` | Consecutive failures that open the circuit |
| `FailureRatio` | `0.5` | Failure share over `SamplingDuration` that opens it |
| `MinimumThroughput` | `20` | Calls needed before `FailureRatio` applies |
| `SamplingDuration` | `00:00:10` | |
| `ConcurrencyLeaseTimeout` | `00:00:30` | How long a dead pod's slot stays taken |
| `ConcurrencyLeaseMaxDuration` | `01:00:00` | Longest a lease holds its slot; raise for long streams |
| `Fallback` | `PerPod` | `PerPod`, `Allow`, `Reject` |
| `PolicyFallbacks` | none | Per policy, e.g. `{ "login": "Reject" }` |

Startup also fails for `QueueLimit > 0` (no queue across pods), a sliding window with more than 100
segments or a window not divisible into whole-millisecond segments (`1` s in `3` is invalid), and a
`PolicyFallbacks` name matching no policy.

### Which connection is used

First that exists: a connection passed in code, then `Configuration` (owned by the package,
`abortConnect=false`), then an `IConnectionMultiplexer` in DI. None fails startup. Connecting starts
with the host and is never awaited, so Redis down doesn't block startup; until the first connect
settles, requests fall back quietly (counted, not logged).

### TLS and credentials

- **TLS:** add `ssl=true` to `Configuration`, and `sslHost=<name>` when the endpoint differs from the
  certificate's name. Managed services with in-transit encryption (Azure Cache for Redis,
  ElastiCache) need it. The certificate is validated against the machine's trust store; an untrusted
  one is a failed connection (the policy falls back, event 100), never an unencrypted one. Outside
  Development, a connection to another host without TLS logs a warning (event 107) once Redis first
  answers, whichever way the connection was supplied; loopback endpoints (a pod-local TLS proxy)
  are not reported.
- **Private CA:** a connection string cannot name a CA, so pass the connection in code. The package
  caps `AsyncTimeout` only on connections it owns, so set it yourself:

  ```csharp
  var redis = ConfigurationOptions.Parse(builder.Configuration["Redis:Endpoint"]!);
  redis.Ssl = true;
  redis.AbortOnConnectFail = false;
  redis.AsyncTimeout = 250;                     // at most CommandTimeout
  redis.TrustIssuer("/etc/redis-ca/ca.crt");
  builder.Services.AddApiPipelineRedisRateLimiting(
      builder.Configuration, _ => ConnectionMultiplexer.Connect(redis));
  ```

- **Credentials:** `user=` and `password=` in `Configuration`, from a secret store. `Configuration`
  is never logged; rejected credentials fall back like an outage (event 100).
- **The `default` user:** while it is enabled without a password, a connection whose credentials are
  rejected stays signed in as `default`, so a wrong password goes unnoticed and runs with
  `default`'s rights. Turn it off (`ACL SETUSER default off`) or give it a password.

### Least-privilege ACL (Redis 6+)

The user needs only the rate-limit keys and these commands. Redis checks commands run inside the
scripts too.

```text
ACL SETUSER orders-api-rl on ><secret> resetkeys ~orders-api:prod:apipipeline:rl:* resetchannels -@all
  +hello +auth +ping +echo +quit +client|setname +client|setinfo +client|id
  +info +cluster|nodes +cluster|slots
  +script|load +evalsha +eval +time
  +get +incrby +pexpire +pttl +pexpireat
  +hgetall +hdel +hincrby +hmget +hset
  +zadd +zrem +zrange +zcard +zremrangebyscore
```

- The key pattern is `{KeyPrefix}:apipipeline:rl:*`.
- Add `tiebreaker=` to `Configuration`: the client otherwise reads `__Booksleeve_TieBreak`, outside
  the key pattern.
- `client|setinfo` exists from Redis 7.2; leave it out on older servers.
- `info` lets each pod check the version and eviction policy (events 104, 105). `cluster|*` matters
  only on a cluster.
- The client's `CONFIG GET` at connect is denied and harmless; `ACL LOG` lists it.
- Redis 5 has no ACLs: use `requirepass` and an instance only rate limiting uses.

### Keys

```text
{KeyPrefix}:apipipeline:rl:{<HMAC-SHA256 of the partition key, 32 hex>}
```

Raw ids and addresses never reach Redis; without the secret the keys can't be matched to callers.
Changing the secret resets every bucket. Every key has a TTL; the braces keep a limiter's keys in
one cluster slot. Run Redis with `maxmemory-policy noeviction`: an evicted key resets a client's
limit. Each pod warns once if the server is older than 5.0 (event 104) or can evict (event 105).

### Sliding window cost

A sliding window reads every segment of its window on each request, in a script on Redis's single
command thread. Each segment adds about 0.7 µs of Redis CPU per request, and that time is taken
from every API sharing the Redis:

| `SegmentsPerWindow` | Redis CPU per request | Decisions per second, one Redis |
| ---: | ---: | ---: |
| 4 | 5 µs | ~135,000 |
| 10 | 6 µs | ~115,000 |
| 60 | 40 µs | ~24,000 |
| 100 (the maximum) | 65 µs | ~15,000 |

Measured on Redis 7.4 (`redis-benchmark`, 50 clients) with every segment holding a count, as under
steady traffic. Keep
`SegmentsPerWindow` at 10 or below; for a smoother limit than that, use a `TokenBucket`, whose
cost does not grow with its settings. Watch `apipipeline.rate_limiting.store.duration`: a rising
p99 with steady traffic means Redis is short of CPU.

### When Redis is unavailable

A failed or slow call never fails the request; the policy's fallback decides it. A process-wide
circuit opens for `FallbackCooldown` after `FailureThreshold` failures in a row, or a `FailureRatio`
failure share (a brownout). While open, `PerPod` and `Allow` policies skip Redis; afterwards one
request probes it. A `WRONGTYPE` reply (one bad key) never opens it.

| `Fallback` | While Redis cannot decide | For |
| --- | --- | --- |
| `PerPod` | Each pod limits alone: up to N× the limit with N pods | Most policies |
| `Allow` | Not limited | Capacity-only policies |
| `Reject` | 429 when its own call fails; ignores the circuit | Login, OTP, payments |

A `Reject` 429's `Retry-After` is the rest of the cooldown (1 s while closed). A starting pod's
`Reject` policies also reject until the first connection is made.

**Choose each policy's fallback before turning Redis on.** The default, `PerPod`, keeps the API
available but fails open by a factor of the replica count. Set `Reject` for every policy that guards
against abuse rather than load, where N× the limit is the attack (credential checks, OTP and
password reset, payments, sign-up):

```json
{
  "ApiPipeline": {
    "RateLimiting": {
      "Redis": {
        "PolicyFallbacks": { "login": "Reject", "otp": "Reject" }
      }
    }
  }
}
```

A `Reject` policy calls Redis on every request, so while Redis is down each of its requests waits
up to `CommandTimeout` before its 429 ([measured](../performance.md#redis-local-docker-not-a-deployment-slo)).
Keep `CommandTimeout` short, and keep `Reject` to the policies that need it.

The circuit logs once when it opens (Warning, with what each fallback does) and once when it closes
(Information). Watch `apipipeline.rate_limiting.store.degraded` and `store.fallback`; see
[OBSERVABILITY.md](../OBSERVABILITY.md#metrics-emitted). ASP.NET Core counts every Redis-limited
request as queued; chart `store.duration` instead
([dashboards](../OBSERVABILITY.md#dashboards-and-alerts-with-the-redis-package)). Runbook:
[Redis down](../RUNBOOK.md#redis-down-limits-are-per-pod).

### Load on Redis

One call per applicable policy, sequential: 1 per request, 2 with `GlobalPolicy` (unless it equals
`DefaultPolicy`), 0 with `DisableRateLimiting()`. Worst-case added latency is `CommandTimeout` per
call. A concurrency lease adds a background release. After Redis rejects a partition, each pod
rejects it locally until `Retry-After`, so a flood costs one call per pod per window.

Size `CommandTimeout` for your network: the default (250 ms) suits Redis in the pods' zone. Before
enabling shared limits in an environment, measure `apipipeline.rate_limiting.store.duration` from a
pod under load, in the same zone and across zones, and keep `CommandTimeout` well above its p99: a
slower call falls back (`store.fallback`, reason `timeout`). Each call is one round trip added to
the request, so include it in the API's latency budget.

Code that calls the limiters directly must use `AcquireAsync`: `AttemptAcquire` always says no
(event 103 warns).

### Custom stores

`IApiPipelineRateLimiterFactory` creates each partition's limiter; `InMemoryRateLimiterFactory` is
the default. Register your own before `AddApiPipelineRateLimiting`. It cannot be combined with the
Redis add-on (startup fails). A wrapper around a replenishing limiter must itself derive from
`ReplenishingRateLimiter` and forward `TryReplenish`.

## Changing configuration

Settings are read once at startup. Changing a policy's limits gives every client a fresh bucket.

Lists merge by position, not name. Over a base `[strict, permissive]`, an environment file with
`[per-user-burst]` renames entry 0 and keeps `permissive`; startup fails when sources name one entry
differently. Override by restating each policy at its position:

```json
"Policies": [
  { "Name": "strict", "PermitLimit": 100 },
  { "Name": "permissive", "PermitLimit": 500 }
]
```

## Troubleshooting

| Symptom | Fix |
| --- | --- |
| No effect | `Enabled` at startup, `WithRateLimiting()` called, no `DisableRateLimiting()`; limits are per pod |
| Everyone shares one bucket | Configure [forwarded headers](forwarded-headers.md#troubleshooting) |
| Every anonymous request gets 429 | [Socket listener](#unix-sockets-and-named-pipes) |
| Legitimate users get 429 | Rejections by `partition_type`: mostly `ip` means forwarded headers; edge 429s mean raise `EdgeRateLimiting:PermitLimit` |
| No `RateLimit` fields on a 429 | Set `EmitRateLimitHeadersOnRejection`; `X-RateLimit-*` [is gone](../migration.md#4a-rate-limiting) |
| `The response headers cannot be modified` after a 429 | Set headers in `Response.OnStarting` |
| Per-pod behaviour with Redis on | `store.degraded` is 1: see the [runbook](../RUNBOOK.md#redis-down-limits-are-per-pod) |
| 429 with `Retry-After` under the limit, Redis policy | Redis unavailable and `Fallback: Reject` |
| Startup error naming a policy | The message says which setting; fix it as stated |

## References

- [Rate limiting middleware in ASP.NET Core](https://learn.microsoft.com/en-us/aspnet/core/performance/rate-limit)
- [RFC 6585 §4: 429 Too Many Requests](https://www.rfc-editor.org/rfc/rfc6585#section-4)
