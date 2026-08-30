# Shared (Redis-backed) rate limiting — design

Status: draft for review · 2026-11-09

## Why

`ApiPipeline:RateLimiting` limits are enforced inside each pod. With N replicas a policy of
"100 requests per minute per user" really allows 100 × N, and the number changes whenever the
deployment scales. The README already warns ("Rate limits are per pod"), but the package offers no
way to enforce a real, service-wide quota. This package is the base for every API in the
organisation, so the quota an operator writes in configuration should be the quota callers get.

Every API already runs Redis (through the internal Caching.NET package), so Redis is the shared
store.

No API uses rate limiting yet. All four limiter kinds must nevertheless be supported, so a team can
pick any of them later without a package change.

## Goals

1. An opt-in add-on package, **`ApiPipeline.NET.RateLimiting.Redis`**, that makes every configured
   rate limit policy shared across all replicas of an API, for all four kinds: `FixedWindow`,
   `SlidingWindow`, `TokenBucket`, `Concurrency`.
2. No change to how policies are written. `ApiPipeline:RateLimiting:Policies`, `DefaultPolicy`,
   partitioning (user, API key, IP, anonymous fallback), named policies, `RequireRateLimiting`,
   hot-reload of limits, 429 responses, `Retry-After` and the rate limit headers all behave as today.
3. **When Redis is unreachable, log a warning and fall back to today's per-pod limits until Redis
   comes back** (decided by the product owner). Nobody is blocked by the outage and nobody is
   unlimited.
4. Independent of Caching.NET: no package reference in either direction.
5. The core package gains no Redis dependency. Apps that don't install the add-on are unaffected.

## Non-goals

- **Edge rate limiting stays per pod.** It exists to shed floods cheaply before authentication; a
  Redis round trip per request would defeat it. Its startup log line keeps saying it is per pod.
- **Queueing.** A Redis-backed limiter never queues a request; it admits or rejects. (Distributed
  queues need cross-pod wake-ups; not worth it for an API base package.) See "QueueLimit" below.
- Sharing a Redis connection with Caching.NET. Caching.NET keeps its connection private today. If it
  ever registers an `IConnectionMultiplexer` in DI, this package picks it up (see "Connection"),
  without either package changing.
- Exact global fairness during a Redis outage. In fallback, limits are per pod again (N × looser).

## Anti-goals (a failure even if it "works")

- A Redis outage turning into an API outage: requests failing, or every request paying a timeout.
- Buckets that are silently *not* shared, for example because pods compute different Redis keys.
  (Today's partition key includes a `HashCode.Combine` signature that is randomized per process;
  see "Keys".)
- Personal data in Redis: raw user IDs, IP addresses or API keys in key names.
- Logs flooding during an outage (one line per request).
- Two APIs sharing one Redis accidentally sharing buckets.

## Constraints

- Same build bar as the rest of the repo: `net10.0`, `TreatWarningsAsErrors`, `IsAotCompatible`,
  source-generated configuration binding and options validation, Central Package Management,
  committed `packages.lock.json`, `CodeStyle.NET`, Source Link, package validation.
- **StackExchange.Redis** as the client, pinned to the version Caching.NET uses (3.3.1 at time of
  writing) so an API that installs both packages resolves one version.
- Configuration follows the 2.0 layout: everything under the `ApiPipeline` section.
- Version: **2.0.0**, in lockstep with `ApiPipeline.NET` and `ApiPipeline.NET.Versioning`
  (*my call*). New package, so `SkipApiCompatBaseline` until it is first published.

## How it fits together

### 1. Extension point in the core package

Today `ConfigureRateLimiterOptions.CreatePartition` builds framework in-memory limiters directly.
Add one small **public** seam in `ApiPipeline.NET` (namespace `ApiPipeline.NET.RateLimiting`):

- A factory abstraction that, given a partition key and the effective `RateLimitPolicy`, returns a
  `System.Threading.RateLimiting.RateLimiter`.
- The default implementation produces exactly today's limiters (moved, not rewritten).
- `ConfigureRateLimiterOptions` resolves the factory from DI and partitions with
  `RateLimitPartition.Get(key, _ => factory.Create(key, policy))`.
- `TryAdd` registration of the default, so the add-on replaces it.

The exact shape (interface vs abstract class, parameter object) is left to the builder; it must be
small, documented, covered by package validation, and usable by a future store other than Redis.

### 2. The add-on package

`src/ApiPipeline.NET.RateLimiting.Redis`, depending on `ApiPipeline.NET` and `StackExchange.Redis`.

Registration, matching the core package's conventions (configuration overload, `Action<TOptions>`
overload, idempotent):

```csharp
builder.Services.AddApiPipeline(builder.Configuration);
builder.Services.AddApiPipelineRedisRateLimiting(builder.Configuration);   // reads ApiPipeline:RateLimiting:Redis
```

Options type `ApiPipelineRedisRateLimitingOptions`, section `ApiPipeline:RateLimiting:Redis`
(the core binder ignores the unknown `Redis` child of `RateLimiting`):

| Setting | Default | Notes |
| --- | --- | --- |
| `Enabled` | `false` | Read once at startup, like every other `Enabled` flag. Off = today's per-pod limiters. |
| `Configuration` | none | StackExchange.Redis connection string. Never logged. |
| `KeyPrefix` | `{ApplicationName}:{EnvironmentName}` | Isolates APIs, and environments, sharing one Redis (so staging traffic never spends production's quota). Validated non-empty, no whitespace. |
| `CommandTimeoutMilliseconds` | `250` | Per limiter call. A slow Redis can't add more than this to a request. |
| `FallbackCooldownSeconds` | `30` | After a failure, use per-pod limits for this long before trying Redis again. |
| `ConcurrencyLeaseTimeoutSeconds` | `300` | A concurrency slot held by a pod that crashed is released after this. Must exceed the longest request. |

Defaults are *my call*; the builder may tune them with reasons.

### 3. Connection

Resolved once, at startup, in this order (*my call*; differs slightly from the order first
discussed, so that an explicit setting always wins over something that merely happens to be in DI):

1. A connection passed in code (`AddApiPipelineRedisRateLimiting(o => ..., IConnectionMultiplexer)`
   or a factory overload; exact overload set left to the builder).
2. `Configuration` connection string → the package opens and owns one multiplexer
   (`abortConnect=false`), disposed with the host.
3. An `IConnectionMultiplexer` already registered in DI by the app (or by a future Caching.NET).
4. None of the above with `Enabled: true` → startup fails with a message naming all three options.

Redis being *down* at startup does not fail startup: the limiter starts in fallback and logs the
warning.

*Builder's choice, recorded after the final review:* the connection (whichever source supplies it) is
opened eagerly by a hosted service at start, without awaiting it. While that first connect is still pending, requests
use the per-pod limits quietly (no circuit opened, no warning), so a normal pod start does not log a
false outage. A connect that fails (any exception from the connection source) is a store failure:
it opens the circuit and logs the warning. A malformed `Configuration` string is rejected at
startup.

### 4. Keys

- Every Redis key is `{KeyPrefix}:apipipeline:rl:{digest}` (exact layout is the builder's), where
  `digest` is a SHA-256-based hash of the core partition key. The partition key holds the user id,
  IP prefix or API key hash, so hashing keeps personal data out of Redis.
- **Deterministic across pods.** `RateLimitPolicySignature` currently uses `HashCode.Combine`, which
  is randomized per process, so each pod would write to different keys and nothing would be shared.
  It must become deterministic (stable string or stable hash of the policy's limits). This is a core
  change; the in-memory store does not care either way.
- Every key carries an expiry, so Redis memory is bounded by active callers.
- **Redis Cluster compatible** (*my call*; managed Redis offerings are often clustered): a script
  that touches more than one key keeps them in one hash slot (hash tag around the digest).

### 5. Limiter semantics per kind

All algorithms run as single Lua scripts (atomic), use **Redis server time** (`TIME`) rather than
pod clocks, and report `RetryAfter` metadata where the kind has one, so `Retry-After` and the
`RateLimit` header stay correct (and become more accurate than today's fixed-window "full window").

| Kind | Required behavior |
| --- | --- |
| `FixedWindow` | `PermitLimit` per `WindowSeconds`; window aligned to first use; `Retry-After` = time to window end. |
| `SlidingWindow` | Same semantics as the framework's `SlidingWindowRateLimiter` (`PermitLimit`, `WindowSeconds`, `SegmentsPerWindow`); bounded memory (no per-request log). Algorithm left to the builder. |
| `TokenBucket` | `PermitLimit` = bucket size, `TokensPerPeriod` per `WindowSeconds`; refill computed from server time. |
| `Concurrency` | At most `PermitLimit` leases in flight across all pods. Lease released when the framework disposes it; a lease from a crashed pod expires after `ConcurrencyLeaseTimeoutSeconds`. |

`AutoReplenishment` has no meaning for time-based Redis limiters and is ignored.

**QueueLimit.** Redis-backed limiters don't queue. With `Redis:Enabled: true`, a policy with
`QueueLimit > 0` **fails startup validation** with a message explaining why (*my call*: silently
ignoring a configured queue would change behavior without telling anyone).

**Sync vs async acquire.** `RateLimiter.AttemptAcquire` is synchronous and must not block on the
network. The builder must confirm against the ASP.NET Core 10 `RateLimitingMiddleware` source that
a non-acquired synchronous attempt is followed by `AcquireAsync`, and design the Redis limiters
around that (no sync-over-async). If the middleware does not behave that way, stop and report
before building around it.

### 6. Fallback when Redis is unreachable

- Each Redis-backed partition has a lazily created per-pod twin built by the default factory
  (identical to today's limiter for that policy and caller).
- A Redis error or timeout on a limiter call → that request is decided by the twin.
- A shared circuit (per process, not per partition) then stays open for `FallbackCooldownSeconds`;
  while open, all limiter calls go straight to the twins with no Redis attempt. After the cooldown
  one call tries Redis; success closes the circuit.
- **Logging:** one `Warning` when the circuit opens ("shared rate limiting unavailable, using
  per-pod limits", with the exception), one `Information` when it closes. Nothing per request.
- Concurrency leases taken in Redis are released in Redis even if the circuit opened meanwhile
  (best effort; expiry covers failures).

### 7. Telemetry

On the existing `ApiPipeline.NET` meter (the add-on records through a small public or
`InternalsVisibleTo` hook — builder's choice, but the meter name stays `ApiPipeline.NET`):

- `apipipeline.ratelimit.store.fallback` — counter, `{request}`, requests decided per pod because
  Redis was unavailable. Tagged with the policy name (bounded by configuration).
- `apipipeline.ratelimit.store.degraded` — observable gauge, `1` while the circuit is open, else `0`.

Existing `apipipeline.ratelimit.rejected` keeps counting rejections from either store.

### 8. Documentation

- `docs/features/rate-limiting.md`: new "Shared limits across pods" section (install, config, key
  layout, fallback behavior, QueueLimit rule, sizing note on Redis load: one call per request).
- README: the "Rate limits are per pod" bullet points to the add-on; feature table row.
- Package README for `ApiPipeline.NET.RateLimiting.Redis`.
- `docs/OPERATIONS.md`: alert on `apipipeline_ratelimit_store_degraded == 1` for > 5 minutes.
- `docs/RUNBOOK.md`: "Redis down: limits are per pod, what to expect".
- `docs/ARCHITECTURE.md` packages table, `CHANGELOG.md`, solution file, CI coverage gate and the
  "no private-feed dependency" assertion extended to the new package.

### 9. Testing

- Unit tests for each kind's semantics against a real Redis (Testcontainers.Redis; the CI runner
  has Docker). No mocked Redis for algorithm tests.
- **Cross-pod test:** two test hosts sharing one Redis container; the combined admitted count
  equals `PermitLimit`, not 2 × `PermitLimit`.
- Fallback test: unreachable Redis → per-pod limits apply, exactly one warning logged, degraded
  gauge reads 1; Redis back → recovery logged, gauge 0.
- Determinism test: partition key / Redis key identical across two processes (or two signature
  computations with different hash seeds).
- Reload test: changing a policy's `PermitLimit` takes effect (new key) as it does in memory.
- Validation tests: `QueueLimit > 0`, missing connection, bad `KeyPrefix`.
- Existing 377 core tests stay green with the default store.

## Left to the builder

- The exact public shape of the core seam and the add-on's overload set.
- The sliding window algorithm (within the semantics above) and Lua script layout.
- Key layout details beyond prefix + hashed partition key.
- How the add-on records metrics on the core meter.
- Whether to use `IHostedService` or lazy init for the owned connection, as long as Redis-down at
  startup does not fail start.

## Open risks to check early

1. `RateLimitingMiddleware` sync/async acquire behavior (section 5).
2. StackExchange.Redis 3.x under `IsAotCompatible` + `TreatWarningsAsErrors`: if it raises trim/AOT
   warnings, report before suppressing anything.
3. Testcontainers in CI: confirm the private-feed authentication step and Docker coexist on the
   GitHub runner.
