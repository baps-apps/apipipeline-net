# Shared (Redis-backed) Rate Limiting Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A new opt-in package, `ApiPipeline.NET.RateLimiting.Redis`, that enforces every configured rate limit policy across all replicas through Redis, for all four limiter kinds, and falls back to today's per-pod limits (with one warning) while Redis is unreachable.

**Architecture:** The core package gains one public seam, `IApiPipelineRateLimiterFactory`, that builds the `RateLimiter` for a partition; the default `InMemoryRateLimiterFactory` produces today's limiters. The add-on replaces it with `RedisRateLimiterFactory`, whose limiters run one atomic Lua script per acquire on the Redis server's clock and delegate to a lazily built in-memory twin while a process-wide circuit is open. Policy configuration, partitioning, 429 responses and headers are untouched.

**Tech Stack:** .NET 10, ASP.NET Core rate limiting (`System.Threading.RateLimiting`), StackExchange.Redis 3.3.1, xUnit v3 on Microsoft.Testing.Platform, FluentAssertions 7.1.0, Testcontainers.Redis 4.15.0 (`redis:7.4-alpine`).

**Spec:** `docs/superpowers/specs/2026-11-09-redis-rate-limiting-design.md`

## Global Constraints

- `net10.0`; `TreatWarningsAsErrors`, `IsAotCompatible`, `Nullable`, `ImplicitUsings`, `EnableConfigurationBindingGenerator` on every `src` project (copy `src/ApiPipeline.NET.Versioning/ApiPipeline.NET.Versioning.csproj` as the template).
- Central Package Management: versions only in `Directory.Packages.props`; commit every `packages.lock.json` change (`dotnet restore ApiPipeline.NET.slnx --force-evaluate`); CI restores with `--locked-mode`.
- `StackExchange.Redis` **3.3.1** (same pin as Caching.NET). No reference to Caching.NET in either direction.
- **No reflection-based Redis APIs:** never `LuaScript.Prepare` with an anonymous parameter object. Use `IDatabase.ScriptEvaluateAsync(string script, RedisKey[] keys, RedisValue[] values)`.
- Every Redis call is bounded with `Task.WaitAsync(TimeSpan.FromMilliseconds(CommandTimeoutMilliseconds))`; a shared multiplexer's own timeouts are not changed.
- Configuration lives at `ApiPipeline:RateLimiting:Redis`. An `IConfiguration` argument that is an `IConfigurationSection` is the pipeline section itself; otherwise its `ApiPipeline` child is read (same rule as the core package).
- Option defaults (spec): `Enabled=false`, `KeyPrefix` = `"{ApplicationName}:{EnvironmentName}"`, `CommandTimeoutMilliseconds=250`, `FallbackCooldownSeconds=30`, `ConcurrencyLeaseTimeoutSeconds=300`.
- Metrics on meter `ApiPipeline.NET` (`ApiPipelineTelemetry.MeterName`): `apipipeline.ratelimit.store.fallback` (counter, unit `{request}`, tag `policy_name`) and `apipipeline.ratelimit.store.degraded` (observable gauge, `1`/`0`).
- Package version **2.0.0**; `<SkipApiCompatBaseline>true</SkipApiCompatBaseline>` until first published.
- Edge rate limiting (`EdgeRateLimitingMiddleware`) is not touched.
- Logging: one `Warning` when the circuit opens, one `Information` when it closes, nothing per request. The connection string is never logged.

## Verified before planning

- **Sync vs async acquire (spec risk 1):** a throwaway .NET 10 app showed `RateLimitingMiddleware` calls `AcquireAsync` after `AttemptAcquire` returns a non-acquired lease, for both the global limiter and a named policy; the request then succeeds. Redis limiters therefore return a non-acquired lease from `AttemptAcquireCore` (no I/O) and do all Redis work in `AcquireAsyncCore`.
- **AOT (spec risk 2):** a library with `IsAotCompatible` + `TreatWarningsAsErrors` referencing StackExchange.Redis 3.3.1 and calling `ScriptEvaluateAsync` builds with 0 warnings.
- **Docker:** available locally (Docker Desktop 29.9) and on GitHub `ubuntu-latest` runners.

## Review Focus

1. **A Redis error that is not a connectivity failure** (`WRONGTYPE` from a key collision, a script error) must fall back like an outage, not return 500. → Task 6, `TokenBucket_KeyOfWrongType_FallsBackInsteadOf500`.
2. **The client cancelling mid-acquire** (`RequestAborted`) must not open the circuit or log the outage warning. → Task 4, `CancelledAcquire_DoesNotOpenTheCircuit`.
3. **Two different APIs sharing one Redis** must not share buckets through the default `KeyPrefix`. → Task 3, `DifferentApplications_DoNotShareBuckets`.
4. **Very long partition identities** (a 2 KB user id) must produce a fixed-length key and work. → Task 3, `LongUserId_ProducesFixedLengthKey`.
5. **A connection string with a password** must never appear in logs, including exception text. → Task 4, `Password_NeverAppearsInLogs`.

---

## File Structure

**Core (`src/ApiPipeline.NET/RateLimiting/`)**
- Create `IApiPipelineRateLimiterFactory.cs` — the public seam.
- Create `InMemoryRateLimiterFactory.cs` — today's limiters, moved out of `ConfigureRateLimiterOptions.CreatePartition`.
- Modify `ConfigureRateLimiterOptions.cs` — partitions via the factory.
- Modify `RateLimitPolicySignature.cs` — deterministic.
- Modify `src/ApiPipeline.NET/Extensions/ApiPipelineServiceCollectionExtensions.cs` (`AddRateLimiterServices`) — `TryAddSingleton` the default factory.
- Modify `src/ApiPipeline.NET/CompatibilitySuppressions.xml` only if package validation demands it (new public types are additive).

**Add-on (`src/ApiPipeline.NET.RateLimiting.Redis/`)**
- `ApiPipeline.NET.RateLimiting.Redis.csproj`, `README.md`
- `ApiPipelineRedisRateLimitingOptions.cs` — options + source-generated validator.
- `ApiPipelineRedisRateLimitingServiceCollectionExtensions.cs` — registration (namespace `Microsoft.Extensions.DependencyInjection`).
- `RedisConnectionSource.cs` — resolves or owns the multiplexer.
- `RedisRateLimitingStartupCheck.cs` — fails start when enabled with no connection source.
- `QueueLimitValidator.cs` — rejects `QueueLimit > 0` when enabled.
- `RedisCircuit.cs` — process-wide circuit, logging, degraded state.
- `RedisRateLimitingMetrics.cs` — the two instruments.
- `RedisKeys.cs` — key derivation.
- `RedisScripts.cs` — the Lua scripts.
- `RedisRateLimiter.cs` — base limiter (sync/async split, timeout, fallback twin, metadata).
- `RedisRateLimiterFactory.cs` — `IApiPipelineRateLimiterFactory` implementation.

**Tests (`tests/ApiPipeline.NET.RateLimiting.Redis.Tests/`)** — separate project so the core suite needs no Docker.
- `ApiPipeline.NET.RateLimiting.Redis.Tests.csproj`, `AssemblyInfo.cs`, `RedisFixture.cs`, `RedisTestHost.cs`, `CapturingLoggerProvider.cs` (records message **and** `exception?.ToString()`), one test class per task.

---

### Task 1: Deterministic policy signature

**Files:**
- Modify: `src/ApiPipeline.NET/RateLimiting/RateLimitPolicySignature.cs`
- Test: `tests/ApiPipeline.NET.Tests/RateLimitPolicySignatureTests.cs` (create)

**Interfaces:**
- Produces: `internal static string RateLimitPolicySignature.Compute(RateLimitPolicy policy)` — unchanged signature; output now `"{name}:{16 lowercase hex}"`, identical in every process.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact]
public void Compute_IsTheSameInEveryProcess()
{
    var policy = new RateLimitPolicy { Name = "strict", Kind = RateLimiterKind.FixedWindow, PermitLimit = 100, WindowSeconds = 60 };
    // Golden value: computed once, pinned. HashCode.Combine is seeded per process and could never pass this.
    RateLimitPolicySignature.Compute(policy).Should().Be(ExpectedGolden);
}

[Theory] // each changed field yields a different signature
[InlineData(nameof(RateLimitPolicy.PermitLimit))] [InlineData(nameof(RateLimitPolicy.WindowSeconds))]
[InlineData(nameof(RateLimitPolicy.Kind))] [InlineData(nameof(RateLimitPolicy.QueueLimit))]
[InlineData(nameof(RateLimitPolicy.SegmentsPerWindow))] [InlineData(nameof(RateLimitPolicy.TokensPerPeriod))]
[InlineData(nameof(RateLimitPolicy.QueueProcessingOrder))] [InlineData(nameof(RateLimitPolicy.AutoReplenishment))]
public void Compute_ChangesWhenALimitChanges(string field) { /* baseline vs one field mutated: NotBe */ }
```

`ExpectedGolden` is the value the new implementation produces for that policy, written into the test after Step 3 and never regenerated.

- [ ] **Step 2: Run, expect the golden test to fail** — `dotnet test --project tests/ApiPipeline.NET.Tests -c Release --filter-class "ApiPipeline.NET.Tests.RateLimitPolicySignatureTests"`.

- [ ] **Step 3: Implement.** Build an invariant-culture string of every field in the existing `HashCode.Combine` call (`Kind`, `PermitLimit`, `QueueLimit`, `WindowSeconds`, `SegmentsPerWindow`, `TokensPerPeriod`, `QueueProcessingOrder`, `AutoReplenishment`; nulls as `-`), SHA-256 it (UTF-8), take the first 8 bytes as `Convert.ToHexStringLower`. Keep the `"{name}:"` prefix (`-` for a blank name). Update the comment: partition keys now leave the process.

- [ ] **Step 4: Pin the golden value, run the class and the full core suite** — expected: all pass (377 + new).

- [ ] **Step 5: Commit** — `fix(rate-limiting): make policy signature deterministic across processes`.

---

### Task 2: Core seam `IApiPipelineRateLimiterFactory`

**Files:**
- Create: `src/ApiPipeline.NET/RateLimiting/IApiPipelineRateLimiterFactory.cs`, `src/ApiPipeline.NET/RateLimiting/InMemoryRateLimiterFactory.cs`
- Modify: `src/ApiPipeline.NET/RateLimiting/ConfigureRateLimiterOptions.cs:26-34,90-215`, `src/ApiPipeline.NET/Extensions/ApiPipelineServiceCollectionExtensions.cs` (`AddRateLimiterServices`)
- Test: `tests/ApiPipeline.NET.Tests/RateLimiterFactoryTests.cs` (create)

**Interfaces:**
- Produces (public, namespace `ApiPipeline.NET.RateLimiting`):
  - `public interface IApiPipelineRateLimiterFactory { RateLimiter Create(string partitionKey, RateLimitPolicy policy); }` — XML docs: called once per partition (the framework caches the limiter); `partitionKey` is stable across processes and already contains the policy signature; implementations must be thread-safe.
  - `public sealed class InMemoryRateLimiterFactory : IApiPipelineRateLimiterFactory` with a public parameterless constructor; `Create` returns the framework limiter for `policy.Kind`, options exactly as today's `CreatePartition` (including the `?? 60`, `?? 4`, `?? 1` fallbacks and the `NotSupportedException`).
- Consumes: Task 1's `RateLimitPolicySignature`.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public async Task CustomFactory_IsUsedForTheDefaultPolicyPartition()
// TestAppBuilder.WithRateLimiting(permitLimit: 5) + configureServices: s.AddSingleton<IApiPipelineRateLimiterFactory>(recording)
// GET /test once → recording.Calls.Should().ContainSingle().Which.Policy.Name.Should().Be("strict");
// recording returns new InMemoryRateLimiterFactory().Create(...) so the request still succeeds (200).

[Fact] public async Task CustomFactory_IsUsedForNamedPolicies()
// endpoint .RequireRateLimiting("strict") → one call, partition key contains "|strict:".

[Fact] public void InMemoryFactory_ProducesTheFrameworkLimiterForEachKind()
// FixedWindow → FixedWindowRateLimiter, SlidingWindow → SlidingWindowRateLimiter,
// TokenBucket → TokenBucketRateLimiter, Concurrency → ConcurrencyLimiter.
```

- [ ] **Step 2: Run, expect compile failure** (types missing).

- [ ] **Step 3: Implement.** Move the body of `CreatePartition` into `InMemoryRateLimiterFactory.Create`. `CreatePartition` becomes `RateLimitPartition.Get(partitionKey, key => factory.Create(key, policy))`; thread `IApiPipelineRateLimiterFactory factory` through `Partition` / `PartitionFor` (constructor-injected into `ConfigureRateLimiterOptions`). `DenyAllRateLimiter` and `GetNoLimiter` partitions stay as they are. In `AddRateLimiterServices`: `services.TryAddSingleton<IApiPipelineRateLimiterFactory, InMemoryRateLimiterFactory>();`.

- [ ] **Step 4: Run the new class and the full core suite** — all pass.

- [ ] **Step 5: Pack the core project with the real baseline** — `dotnet pack src/ApiPipeline.NET -c Release -o <scratch>`; expected: success (new types are additive). If package validation reports a suppression change, regenerate per `Directory.Build.targets` and commit it.

- [ ] **Step 6: Commit** — `feat(rate-limiting): add IApiPipelineRateLimiterFactory seam`.

---

### Task 3: Add-on package skeleton, options, registration, keys, fixed window

The first task that touches Redis: it brings up the project, the test harness, the connection, the base limiter and the simplest kind, end to end.

**Files:**
- Create: everything under `src/ApiPipeline.NET.RateLimiting.Redis/` except `RedisCircuit.cs` and `RedisRateLimitingMetrics.cs` (Task 4); test project files; `tests/.../FixedWindowTests.cs`, `tests/.../RegistrationTests.cs`, `tests/.../KeyTests.cs`.
- Modify: `Directory.Packages.props` (`StackExchange.Redis` 3.3.1, `Testcontainers.Redis` 4.15.0), `ApiPipeline.NET.slnx` (both projects). The add-on uses only public core APIs: no `InternalsVisibleTo`.

**Interfaces:**
- Consumes: `IApiPipelineRateLimiterFactory`, `InMemoryRateLimiterFactory` (Task 2); `ApiPipelineConfigurationKeys.Section`, `.RateLimiting`; `ApiPipelineRateLimitingOptions`, `RateLimitPolicy`, `RateLimiterKind`.
- Produces:
  - `public sealed class ApiPipelineRedisRateLimitingOptions` (namespace `ApiPipeline.NET.RateLimiting.Redis`): `bool Enabled`, `string? Configuration`, `string? KeyPrefix` (null = default), `[Range(50, 10_000)] int CommandTimeoutMilliseconds = 250`, `[Range(1, 3600)] int FallbackCooldownSeconds = 30`, `[Range(1, 86_400)] int ConcurrencyLeaseTimeoutSeconds = 300`. `IValidatableObject`: a non-null `KeyPrefix` must be non-empty with no whitespace. Source-generated `[OptionsValidator]`.
  - `public static class RedisRateLimitingConfigurationKeys { public const string Redis = "Redis"; }` (relative to `ApiPipeline:RateLimiting`).
  - Extensions (namespace `Microsoft.Extensions.DependencyInjection`, class `ApiPipelineRedisRateLimitingServiceCollectionExtensions`):
    - `AddApiPipelineRedisRateLimiting(this IServiceCollection, IConfiguration configuration)`
    - `AddApiPipelineRedisRateLimiting(this IServiceCollection, Action<ApiPipelineRedisRateLimitingOptions> configure)`
    - `AddApiPipelineRedisRateLimiting(this IServiceCollection, IConfiguration configuration, Func<IServiceProvider, IConnectionMultiplexer> connection)`
    All idempotent (bind once, marker-type claim as in the core package); all `services.Replace(ServiceDescriptor.Singleton<IApiPipelineRateLimiterFactory, RedisRateLimiterFactory>())`.
  - `internal sealed class RedisConnectionSource : IAsyncDisposable` — `Task<IDatabase> GetDatabaseAsync()`. Order (spec): explicit `Func` → `Configuration` (owned, `ConfigurationOptions.Parse(..)` with `AbortOnConnectFail = false`, connected lazily; a faulted connect is not cached) → `IConnectionMultiplexer` from DI. Disposes only an owned multiplexer.
  - `internal sealed class RedisRateLimitingStartupCheck : IHostedService` — when `Enabled` and none of the three sources exists, throws `InvalidOperationException` naming all three (`AddApiPipelineRedisRateLimiting(configuration, sp => ...)`, `ApiPipeline:RateLimiting:Redis:Configuration`, a registered `IConnectionMultiplexer`). Does **not** connect.
  - `internal sealed class QueueLimitValidator : IValidateOptions<ApiPipelineRateLimitingOptions>` — when Redis `Enabled`, fails for each policy with `QueueLimit > 0`: `"Policies[{i}] '{name}': QueueLimit must be 0 when ApiPipeline:RateLimiting:Redis:Enabled is true. Shared (Redis) limiters admit or reject; they cannot hold a request in a queue across pods."`
  - `internal static class RedisKeys { public static RedisKey For(string prefix, string partitionKey); }` → `"{prefix}:apipipeline:rl:{" + 32 lowercase hex of SHA-256(partitionKey) + "}"` (hash tag braces keep any future multi-key script in one slot).
  - `internal abstract class RedisRateLimiter : RateLimiter` — `AttemptAcquireCore` returns a cached non-acquired lease (no I/O); `AcquireAsyncCore` runs `ExecuteAsync`, bounded by the timeout; result `{allowed (0|1), retryAfterMs}` → lease with `MetadataName.RetryAfter` when rejected and `retryAfterMs > 0`; `IdleDuration` = time since last acquire (`TimeProvider`), `null` while a concurrency lease is outstanding. `protected abstract Task<RedisResult> ExecuteAsync(IDatabase db, RedisKey key, CancellationToken ct)`. Twin: `Lazy<RateLimiter>` from `InMemoryRateLimiterFactory`. (Task 4 wires the circuit into the failure path; in this task a Redis failure simply propagates.)
  - `AutoReplenishment` is ignored by every Redis limiter (time is computed on the server); say so in its XML docs.
  - `internal sealed class RedisRateLimiterFactory : IApiPipelineRateLimiterFactory` — when Redis `Enabled` is false (read once from `IOptions<>`), delegates every call to `InMemoryRateLimiterFactory`; otherwise returns the kind's Redis limiter.
  - `RedisScripts.FixedWindow` (ARGV: permitLimit, windowMs):

    ```lua
    local c = redis.call('INCR', KEYS[1])
    if c == 1 then redis.call('PEXPIRE', KEYS[1], ARGV[2]) end
    if c > tonumber(ARGV[1]) then return {0, redis.call('PTTL', KEYS[1])} end
    return {1, 0}
    ```

- [ ] **Step 1: Test harness.** `RedisFixture : IAsyncLifetime` starts one `new RedisBuilder("redis:7.4-alpine").Build()` per assembly (`[assembly: AssemblyFixture(typeof(RedisFixture))]`, serial parallelization as in the core tests) and exposes `ConnectionString`. `RedisTestHost.CreateAsync(string connectionString, Dictionary<string,string?> config, string applicationName = "api-a", CapturingLoggerProvider? logs = null)` builds a `WebApplication` on TestServer with `AddApiPipeline` + `AddApiPipelineRedisRateLimiting(builder.Configuration)`, `UseApiPipeline(p => p.WithRateLimiting())`, `MapGet("/test")`, and `ApiPipeline:RateLimiting:AnonymousFallback = RateLimit` (TestServer has no remote IP). Each test sets a unique `KeyPrefix`.

- [ ] **Step 2: Write the failing tests**

```csharp
// FixedWindowTests
[Fact] public async Task TwoHosts_ShareOneBucket()
// both hosts: Redis Enabled, FixedWindow PermitLimit=5, WindowSeconds=60, same KeyPrefix.
// 4 requests to host A, 4 to host B → exactly 5 × 200 and 3 × 429 in total.

[Fact] public async Task Rejection_RetryAfter_IsWithinTheWindow()
// PermitLimit=1, WindowSeconds=60; second request 429 with Retry-After in [1, 60].

[Fact] public async Task ReloadedLimit_TakesEffect()
// reload PermitLimit 1 → 3 via IConfigurationRoot.Reload(): three more 200s.

// RegistrationTests
[Fact] public void Options_BindFrom_ApiPipeline_RateLimiting_Redis()
[Fact] public void Options_FromSection_ReadAsThePipelineSection()        // GetSection("Platform:Api") → Platform:Api:RateLimiting:Redis
[Fact] public void KeyPrefix_Default_IsApplicationAndEnvironment()        // "api-a:Development"
[Fact] public async Task Enabled_WithNoConnectionSource_FailsStartup()   // message names all three sources
[Fact] public async Task Enabled_WithQueueLimit_FailsStartup()           // message contains "QueueLimit must be 0"
[Fact] public async Task KeyPrefix_WithWhitespace_FailsStartup()         // "api a" → OptionsValidationException naming KeyPrefix
[Fact] public async Task Disabled_UsesPerPodLimits_AndNeverConnects()    // Configuration "127.0.0.1:1"; 2 hosts, PermitLimit=2 → 4 × 200
[Fact] public async Task ExplicitConnection_WinsOverConfigurationAndDi()  // Func returns fixture mux; Configuration points at 127.0.0.1:1 → shared limit enforced

// KeyTests
[Fact] public async Task Keys_ContainNoRawIdentity()                     // authenticated user "alice@example.com": SCAN prefix* → no key contains "alice"
[Fact] public async Task DifferentApplications_DoNotShareBuckets()        // hosts with applicationName api-a / api-b, default prefix, PermitLimit=1 → both get 200
[Fact] public void LongUserId_ProducesFixedLengthKey()                   // RedisKeys.For("p", new string('u', 2048)).ToString().Length == "p:apipipeline:rl:{}".Length + 32
```

- [ ] **Step 3: Run, expect compile failure.**

- [ ] **Step 4: Implement** the files listed under Interfaces.

- [ ] **Step 5: Run the Redis test project** — `dotnet test --project tests/ApiPipeline.NET.RateLimiting.Redis.Tests -c Release`; all pass. Run the full solution; core suite unaffected.

- [ ] **Step 6: Restore lock files, build the solution, commit** — `feat(rate-limiting): add ApiPipeline.NET.RateLimiting.Redis with shared fixed window`.

---

### Task 4: Circuit, fallback, metrics

**Files:**
- Create: `src/ApiPipeline.NET.RateLimiting.Redis/RedisCircuit.cs`, `RedisRateLimitingMetrics.cs`
- Modify: `RedisRateLimiter.cs`, `ApiPipelineRedisRateLimitingServiceCollectionExtensions.cs`
- Test: `tests/.../FallbackTests.cs`

**Interfaces:**
- Produces:
  - `internal sealed class RedisCircuit` (singleton; `TimeProvider`, `ILogger<RedisCircuit>`, options): `bool TryEnter()` — true when closed, or when the cooldown has elapsed and this caller wins the single half-open probe (`Interlocked`); `void ReportSuccess()` — closes, logs `Information` once ("Shared rate limiting restored; limits are shared across pods again."); `void ReportFailure(Exception ex)` — opens for `FallbackCooldownSeconds`, logs `Warning` with `ex` only on a closed→open transition ("Shared rate limiting is unavailable; using per-pod limits until Redis responds. Limits are N times looser with N replicas."); `bool IsOpen`.
  - `internal sealed class RedisRateLimitingMetrics` (from `IMeterFactory`, meter `ApiPipelineTelemetry.MeterName`): `void Fallback(string policyName)`; observable gauge reading `circuit.IsOpen ? 1 : 0`.
- `RedisRateLimiter.AcquireAsyncCore` becomes: `TryEnter()` false → metrics + twin; else Redis call → success `ReportSuccess()`; `RedisException`, `RedisTimeoutException`, `RedisConnectionException`, `TimeoutException`, `ObjectDisposedException` (connection) → `ReportFailure`, metrics, twin. `OperationCanceledException` when `cancellationToken.IsCancellationRequested` propagates and **does not** touch the circuit.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public async Task Unreachable_FallsBackToPerPodLimits_AndWarnsOnce()
// Configuration "127.0.0.1:1,connectTimeout=200"; PermitLimit=2; 5 requests on one host → 2 × 200, 3 × 429;
// logs.Count(contains "Shared rate limiting is unavailable") == 1.

[Fact] public async Task Outage_ThenRecovery_LogsRestoredAndClearsGauge()
// fixture container PauseAsync → request (fallback, gauge 1) → UnpauseAsync → wait FallbackCooldownSeconds(=1)+0.5s
// → request → logs contain "Shared rate limiting restored"; gauge 0. (Use a MeterListener on the gauge.)

[Fact] public async Task WhileOpen_RedisIsNotCalled()
// after the first failure, 20 more requests complete in a total time below CommandTimeoutMilliseconds,
// which is only possible if none of them waited on Redis.

[Fact] public async Task FallbackCounter_IsTaggedWithPolicy()
// MeterListener on apipipeline.ratelimit.store.fallback: tag policy_name == "strict".

[Fact] public async Task CancelledAcquire_DoesNotOpenTheCircuit()      // Review Focus 2
// direct limiter: AcquireAsync with an already-cancelled token → OperationCanceledException; circuit.IsOpen false; no warning.

[Fact] public async Task Password_NeverAppearsInLogs()                  // Review Focus 5
// Configuration "127.0.0.1:1,password=s3cr3t-pw,connectTimeout=200"; one request;
// no captured message or exception text contains "s3cr3t-pw".

[Fact] public async Task RedisDownAtStartup_DoesNotFailStart()
```

- [ ] **Step 2: Run, expect failures.**
- [ ] **Step 3: Implement** per Interfaces. If `Password_NeverAppearsInLogs` fails because StackExchange.Redis puts the configuration in exception text, log a sanitized exception (type + endpoint) instead of the raw one — do not drop the warning.
- [ ] **Step 4: Run the Redis test project** — all pass.
- [ ] **Step 5: Commit** — `feat(rate-limiting): fall back to per-pod limits while Redis is unavailable`.

---

### Task 5: Sliding window

**Files:** Modify `RedisScripts.cs`, `RedisRateLimiterFactory.cs`; create `RedisSlidingWindowRateLimiter` in `RedisRateLimiter.cs` or its own file; test `tests/.../SlidingWindowTests.cs`.

**Interfaces:** `RedisScripts.SlidingWindow` (ARGV: permitLimit, windowMs, segments). Same `{allowed, retryAfterMs}` contract.

Algorithm (segmented counters in one hash; server time; bounded memory — one field per segment):

```lua
local t = redis.call('TIME')
local now = tonumber(t[1]) * 1000 + math.floor(tonumber(t[2]) / 1000)
local limit, window, segments = tonumber(ARGV[1]), tonumber(ARGV[2]), tonumber(ARGV[3])
local seg = math.floor(window / segments)
local current = math.floor(now / seg)
local oldest = current - segments + 1
local fields = redis.call('HGETALL', KEYS[1])
local total, earliest = 0, nil
for i = 1, #fields, 2 do
  local idx, n = tonumber(fields[i]), tonumber(fields[i + 1])
  if idx < oldest then redis.call('HDEL', KEYS[1], fields[i])
  else total = total + n; if earliest == nil or idx < earliest then earliest = idx end end
end
if total >= limit then return {0, (earliest + segments) * seg - now} end
redis.call('HINCRBY', KEYS[1], current, 1)
redis.call('PEXPIRE', KEYS[1], window + seg)
return {1, 0}
```

- [ ] **Step 1: Failing tests** — `TwoHosts_ShareOneBucket` (PermitLimit=4, WindowSeconds=60, SegmentsPerWindow=4 → 4 × 200 then 429 across hosts); `PermitsReturn_AsSegmentsSlideOut` (WindowSeconds=2, SegmentsPerWindow=2, PermitLimit=2: 2 × 200, 429, wait 2.2 s, 200); `Rejection_RetryAfter_IsAtMostTheWindow`.
- [ ] **Step 2: Run, expect failures.** **Step 3: Implement.** **Step 4: Run — pass.** **Step 5: Commit** — `feat(rate-limiting): shared sliding window limiter`.

---

### Task 6: Token bucket

**Files:** Modify `RedisScripts.cs`, `RedisRateLimiterFactory.cs`; limiter class; test `tests/.../TokenBucketTests.cs`.

**Interfaces:** `RedisScripts.TokenBucket` (ARGV: tokenLimit = `PermitLimit`, tokensPerPeriod, periodMs = `WindowSeconds` × 1000).

Algorithm (hash `tokens`, `ts`; whole periods only, matching the framework's period-based replenishment):

```lua
local t = redis.call('TIME')
local now = tonumber(t[1]) * 1000 + math.floor(tonumber(t[2]) / 1000)
local cap, rate, period = tonumber(ARGV[1]), tonumber(ARGV[2]), tonumber(ARGV[3])
local h = redis.call('HMGET', KEYS[1], 'tokens', 'ts')
local tokens, ts = tonumber(h[1]) or cap, tonumber(h[2]) or now
local periods = math.floor((now - ts) / period)
if periods > 0 then tokens = math.min(cap, tokens + periods * rate); ts = ts + periods * period end
local ttl = math.ceil(cap / rate) * period + period
if tokens >= 1 then
  redis.call('HSET', KEYS[1], 'tokens', tokens - 1, 'ts', ts); redis.call('PEXPIRE', KEYS[1], ttl)
  return {1, 0}
end
redis.call('HSET', KEYS[1], 'tokens', tokens, 'ts', ts); redis.call('PEXPIRE', KEYS[1], ttl)
return {0, period - (now - ts)}
```

- [ ] **Step 1: Failing tests** — `TwoHosts_ShareOneBucket` (PermitLimit=3, TokensPerPeriod=1, WindowSeconds=60 → 3 × 200 then 429 across hosts); `Tokens_Replenish_PerPeriod` (WindowSeconds=1: drain, wait 1.2 s, one 200 then 429); `TokenBucket_KeyOfWrongType_FallsBackInsteadOf500` (Review Focus 1: `SET` the computed key to a string first → request returns 200 via fallback, one warning).
- [ ] **Steps 2–5** as Task 5. Commit — `feat(rate-limiting): shared token bucket limiter`.

---

### Task 7: Concurrency

**Files:** Modify `RedisScripts.cs`, `RedisRateLimiterFactory.cs`; limiter + lease classes; test `tests/.../ConcurrencyTests.cs`.

**Interfaces:**
- `RedisScripts.ConcurrencyAcquire` (ARGV: permitLimit, leaseTimeoutMs, leaseId): sorted set, score = server-time expiry.

  ```lua
  local t = redis.call('TIME')
  local now = tonumber(t[1]) * 1000 + math.floor(tonumber(t[2]) / 1000)
  redis.call('ZREMRANGEBYSCORE', KEYS[1], '-inf', now)
  if redis.call('ZCARD', KEYS[1]) >= tonumber(ARGV[1]) then return {0, 0} end
  redis.call('ZADD', KEYS[1], now + tonumber(ARGV[2]), ARGV[3])
  redis.call('PEXPIRE', KEYS[1], tonumber(ARGV[2]))
  return {1, 0}
  ```
- `RedisScripts.ConcurrencyRelease`: `ZREM KEYS[1] ARGV[1]` (plain `SortedSetRemoveAsync` is fine).
- The acquired lease's `Dispose` releases best-effort (fire-and-forget, bounded by the timeout, exceptions swallowed and not reported to the circuit). A lease obtained from the twin during fallback releases the twin.
- No `RetryAfter` metadata for concurrency (matches the framework; `RateLimitRejection` already handles its absence).

- [ ] **Step 1: Failing tests** — `TwoHosts_ShareTheInFlightLimit` (PermitLimit=2; endpoint awaits a `TaskCompletionSource`; start 2 on host A, 1 on host B → B gets 429; release → B's retry 200); `ReleasedLease_FreesTheSlot`; `CrashedPodLease_ExpiresAfterTimeout` (acquire through the factory directly and never dispose; `ConcurrencyLeaseTimeoutSeconds=1`; after 1.2 s a new acquire succeeds).
- [ ] **Steps 2–5** as Task 5. Commit — `feat(rate-limiting): shared concurrency limiter`.

---

### Task 8: Documentation, CI, packaging

**Files:**
- Modify: `docs/features/rate-limiting.md` (new section "Shared limits across pods": install, `ApiPipeline:RateLimiting:Redis` table with the Global Constraints defaults, connection order, key layout and hashing, fallback behavior and logs, `QueueLimit` rule, Redis load = one call per request), `README.md` (the "Rate limits are per pod" bullet links the add-on; feature table row; package list), `docs/OPERATIONS.md` (alert: `apipipeline_ratelimit_store_degraded == 1` for 5 minutes; dashboard row for `apipipeline_ratelimit_store_fallback_total`), `docs/RUNBOOK.md` ("Redis down: limits are per pod"), `docs/OBSERVABILITY.md` (metrics table rows), `docs/ARCHITECTURE.md` (packages table; the seam), `CHANGELOG.md` (2.0.0 Added: the package and `IApiPipelineRateLimiterFactory`; Changed: deterministic policy signature), `.github/workflows/ci.yml` (coverage gate `ApiPipeline.NET.RateLimiting.Redis=80`), `.github/workflows/release.yml` only if it lists projects explicitly.
- Create: `src/ApiPipeline.NET.RateLimiting.Redis/README.md` (packed as the package readme).

- [ ] **Step 1:** Write the docs. Every JSON sample uses the `ApiPipeline` layout and parses (`python3 -I -c 'import json,sys; json.load(open(sys.argv[1]))'` on extracted blocks).
- [ ] **Step 2:** Full verification — `dotnet restore ApiPipeline.NET.slnx --locked-mode`; `dotnet build ApiPipeline.NET.slnx -c Release` (0 warnings); `dotnet test --solution ApiPipeline.NET.slnx -c Release --coverage --coverage-output-format cobertura`; `python3 -I scripts/check-coverage.py <report> ApiPipeline.NET=85 ApiPipeline.NET.Versioning=75 ApiPipeline.NET.RateLimiting.Redis=80`; `dotnet pack ApiPipeline.NET.slnx -c Release -o <scratch>` succeeds; the new nuspec depends only on `ApiPipeline.NET` and `StackExchange.Redis` (no `CodeStyle.NET`).
- [ ] **Step 3: Commit** — `docs(rate-limiting): document shared Redis limits; CI coverage gate`.
