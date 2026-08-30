# ApiPipeline.NET.RateLimiting.Redis

Shares [ApiPipeline.NET](https://github.com/baps-apps/apipipeline-net) rate limit policies across
every replica through Redis 5.0+, so `PermitLimit: 100` means 100 across all pods instead of per
pod.

```bash
dotnet add package ApiPipeline.NET.RateLimiting.Redis
```

Requires StackExchange.Redis 3.4.35 or later. An application that references an older version itself
fails restore with `NU1605`: raise its reference to 3.4.35 or later (from 2.x, a major upgrade of the
client; see its release notes).

```csharp
builder.Services.AddApiPipeline(builder.Configuration);
builder.Services.AddApiPipelineRedisRateLimiting(builder.Configuration);
```

```json
{
  "ApiPipeline": {
    "RateLimiting": {
      "Enabled": true,
      "DefaultPolicy": "strict",
      "Policies": [ { "Name": "strict", "Kind": "FixedWindow", "PermitLimit": 100, "Window": "00:01:00" } ],
      "Redis": {
        "Enabled": true,
        "Configuration": "redis:6379",
        "KeyPrefix": "orders-api:prod",
        "PartitionKeySecret": "<from your secret store>"
      }
    }
  }
}
```

- Policies are unchanged, except `QueueLimit` must be `0`.
- `KeyPrefix` and `PartitionKeySecret` are required outside Development.
- Redis down never fails a request or startup: each policy falls back (`PerPod` by default, or
  `Allow`/`Reject` per policy) behind a circuit breaker.
- `PerPod` allows N× the limit with N pods while Redis is down. Set
  `"PolicyFallbacks": { "login": "Reject" }` for every abuse-sensitive policy (login, OTP,
  payments); each of its requests then waits up to `CommandTimeout` during an outage.
- Run Redis with `maxmemory-policy noeviction`.
- Keep a sliding window's `SegmentsPerWindow` at 10 or below (startup fails above 100): each request
  reads every segment on Redis, so 100 segments cost about 65 µs of Redis CPU per request, shared by
  every API on it.
- Use TLS (`ssl=true`), credentials from a secret store, and a dedicated ACL user limited to the
  rate-limit keys; turn off Redis's `default` user, or a rejected password goes unnoticed. The
  command list and private-CA setup:
  [TLS and credentials](https://github.com/baps-apps/apipipeline-net/blob/main/docs/features/rate-limiting.md#tls-and-credentials).
- Set `CommandTimeout` from the measured `store.duration` p99 in your network.
- Call limiters with `AcquireAsync`; `AttemptAcquire` always says no.

Options, keys, failure behaviour and metrics:
[Shared limits across pods](https://github.com/baps-apps/apipipeline-net/blob/main/docs/features/rate-limiting.md#shared-limits-across-pods).
