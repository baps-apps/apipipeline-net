# Complete Feature Benchmark Coverage Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Extend the single BenchmarkDotNet executable with trustworthy request-path, startup, and live-Redis measurements for every feature named in the coverage gap.

**Architecture:** Keep direct `RequestDelegate` benchmarks for middleware, pair every scenario with a like-for-like baseline, and use setup/cleanup guards to prove the intended path stayed active. Measure request-limit and version-deprecation registration as cold-start operations. Start one pinned Redis 7.4 Testcontainer in the parent process and pass its connection string to BenchmarkDotNet children.

**Tech Stack:** .NET 10, ASP.NET Core, BenchmarkDotNet 0.15.8, Asp.Versioning 10.2.x, StackExchange.Redis 3.3.1, Testcontainers.Redis 4.15.0.

**Spec:** `docs/superpowers/specs/2026-11-12-benchmark-feature-coverage-design.md`

## Global Constraints

- Docker is mandatory; Docker unavailability fails the benchmark command instead of skipping Redis.
- Use the repository-required .NET SDK 10.0.403. The current host does not have it, so execution
  must provision it in a task-scoped location rather than editing `global.json` or silently using an
  older SDK.
- Use `redis:7.4-alpine@sha256:c5d34a98396a4775e223c74e929162c35c5a2182901ba26b50ade939941ce14b`.
- Keep Docker startup, Redis connection, and Lua warmup outside measured methods.
- Every request benchmark has a comparable baseline; short-circuit ratios are labeled whole-request comparisons.
- Every setup proves the feature is active; cleanup proves mutable state did not drift during measurement.
- Cache misses, server rejections, and limiter paths use bounded invocation counts/iteration setup.
- Do not change production code to support the benchmark.
- The worktree already contains user changes, including `perf/ApiPipeline.NET.Perf/MiddlewareBenchmarks.cs` and `docs/performance.md`; preserve them and do not commit task changes from this dirty tree unless the user separately authorizes it.

## Review Focus

- Docker missing or inaccessible: fail before BenchmarkDotNet runs with a message naming Docker, covered by Task 6's startup failure check.
- Cache middleware silently bypassed: setup must prove a miss stores and a hit avoids the terminal, covered by Task 3.
- Stateful limit/cache windows drift during a run: fixed invocation counts plus cleanup invariants must fail the run, covered by Tasks 3, 4, and 6.
- Timeout middleware silently disabled: the active terminal must observe `IHttpRequestTimeoutFeature` and a cancellable token while the baseline does not, covered by Task 1.
- Redis server denial confused with the package's local deny cache: command counts must prove one Redis call versus zero, covered by Task 6.

---

### Task 1: Shared harness, forwarded headers, CORS, and request timeouts

**Files:**
- Create: `perf/ApiPipeline.NET.Perf/BenchmarkPipeline.cs`
- Create: `perf/ApiPipeline.NET.Perf/ForwardedHeadersBenchmarks.cs`
- Create: `perf/ApiPipeline.NET.Perf/CorsBenchmarks.cs`
- Create: `perf/ApiPipeline.NET.Perf/RequestTimeoutBenchmarks.cs`

**Interfaces:**
- Produces: `BenchmarkPipeline.BuildProvider(IConfiguration, Action<IServiceCollection, IConfiguration>) -> ServiceProvider`.
- Produces: `BenchmarkPipeline.BuildDelegate(IServiceProvider, Action<IApplicationBuilder>, RequestDelegate) -> RequestDelegate`.
- Produces: `BenchmarkPipeline.CreateContext(IServiceProvider, string, PathString, string) -> DefaultHttpContext`.
- Produces: `BenchmarkPipeline.StartHostedServices(IServiceProvider) -> void` and `BenchmarkPipeline.Require(bool, string) -> void`.
- Produces benchmark classes `ForwardedHeadersBenchmarks`, `CorsGetBenchmarks`, `CorsPreflightBenchmarks`, and `RequestTimeoutBenchmarks`.

- [ ] **Step 1: Prove the coverage is absent**

Run: `dotnet run -c Release --project perf/ApiPipeline.NET.Perf -- --list flat | rg 'ForwardedHeadersBenchmarks|Cors(Get|Preflight)Benchmarks|RequestTimeoutBenchmarks'`

Expected: no matching benchmark names.

- [ ] **Step 2: Implement `BenchmarkPipeline`**

Register quiet logging, the supplied configuration, and a Production `IHostEnvironment`; build/dispose ordinary providers, create standardized HTTPS contexts with request services, and start package `IHostedService` startup checks exactly once in setup.

- [ ] **Step 3: Implement forwarded-header scenarios**

Add `Baseline_NoMiddleware`, `ForwardedHeaders_NoHeaders`, and `ForwardedHeaders_Applied`. Use a loopback peer and validate that the active path changes the address to `203.0.113.42` and scheme to `https`.

- [ ] **Step 4: Implement separate CORS GET and preflight baseline groups**

Use production explicit origin `https://allowed.example.com`. `CorsGetBenchmarks` compares routed GET responses; `CorsPreflightBenchmarks` compares identical OPTIONS requests and proves preflight adds allow headers without invoking the terminal.

- [ ] **Step 5: Implement timeout pass-through coverage**

Add `Baseline_NoTimeoutFeature` and `RequestTimeout_Enabled`. The baseline terminal asserts no `IHttpRequestTimeoutFeature`; the active terminal asserts the feature exists and exposes a cancellable token.

- [ ] **Step 6: Run focused dry benchmarks**

Run: `dotnet run -c Release --project perf/ApiPipeline.NET.Perf -- --filter '*ForwardedHeadersBenchmarks*' '*Cors*Benchmarks*' '*RequestTimeoutBenchmarks*' --job Dry`

Expected: all nine rows execute, semantic setup checks pass, and no row is `NA`.

---

### Task 2: Response compression with a controlled payload

**Files:**
- Create: `perf/ApiPipeline.NET.Perf/CountingWriteStream.cs`
- Create: `perf/ApiPipeline.NET.Perf/ResponseCompressionBenchmarks.cs`

**Interfaces:**
- Consumes: `BenchmarkPipeline` from Task 1.
- Produces: `CountingWriteStream.BytesWritten` and `CountingWriteStream.Reset()`.
- Produces: `ResponseCompressionBenchmarks.Baseline_Uncompressed4KB()` and `ResponseCompressionBenchmarks.Gzip_4KB()`.

- [ ] **Step 1: Prove compression coverage is absent**

Run: `dotnet run -c Release --project perf/ApiPipeline.NET.Perf -- --filter '*ResponseCompressionBenchmarks*' --job Dry`

Expected: no matching benchmark is executed.

- [ ] **Step 2: Implement a non-buffering byte-counting response sink**

Override synchronous and asynchronous writes without retaining payload bytes; `Reset()` sets the count to zero before reuse.

- [ ] **Step 3: Implement paired 4 KiB compression scenarios**

Both methods create the same HTTPS context, send `Accept-Encoding: gzip`, and write the same static compressible 4 KiB payload. Setup proves the active response uses gzip and writes fewer bytes than the baseline.

Set `Content-Type: text/plain; charset=utf-8` before the write so the framework selects a
compressible MIME type deterministically.

- [ ] **Step 4: Run the focused dry benchmark**

Run: `dotnet run -c Release --project perf/ApiPipeline.NET.Perf -- --filter '*ResponseCompressionBenchmarks*' --job Dry`

Expected: two successful rows and no semantic-validation failure.

---

### Task 3: Response-cache and output-cache hit/miss coverage

**Files:**
- Create: `perf/ApiPipeline.NET.Perf/CachingBenchmarks.cs`

**Interfaces:**
- Consumes: `BenchmarkPipeline` from Task 1.
- Produces: `ResponseCachingHitBenchmarks`, `ResponseCachingMissBenchmarks`, `OutputCachingHitBenchmarks`, and `OutputCachingMissBenchmarks`.
- Produces methods `Baseline_HitHarness`, `Cache_Hit`, `Baseline_MissHarness`, and `Cache_Miss` in their respective groups.

- [ ] **Step 1: Prove cache coverage is absent**

Run: `dotnet run -c Release --project perf/ApiPipeline.NET.Perf -- --filter '*Caching*Benchmarks*' --job Dry`

Expected: no matching benchmark is executed.

- [ ] **Step 2: Implement response-cache hit and miss groups**

Write an explicit `Cache-Control: public, max-age=86400` response. Pre-populate the hit key and assert repeated hits do not increment the terminal count. Give miss methods `[InvocationCount(1), UnrollFactor(1)]`; iteration setup assigns a new key to both feature and baseline harnesses.

- [ ] **Step 3: Implement output-cache hit and miss groups**

Attach an endpoint whose metadata contains `Microsoft.AspNetCore.OutputCaching.OutputCacheAttribute`,
pre-populate the hit key, and assert the terminal is skipped. Bound misses exactly as for response
caching.

- [ ] **Step 4: Add post-measurement state guards**

Global cleanup asserts hit terminal counts did not change after pre-population, miss counts equal measured invocations, and no one-day entry expired.

- [ ] **Step 5: Run focused dry benchmarks**

Run: `dotnet run -c Release --project perf/ApiPipeline.NET.Perf -- --filter '*Caching*Benchmarks*' --job Dry`

Expected: eight successful rows; hit guards prove terminal bypass and miss guards prove storage work.

---

### Task 4: Edge rate-limiter allowed and rejected paths

**Files:**
- Create: `perf/ApiPipeline.NET.Perf/EdgeRateLimitingBenchmarks.cs`

**Interfaces:**
- Consumes: `BenchmarkPipeline` from Task 1.
- Produces: `EdgeRateLimitingAllowedBenchmarks` and `EdgeRateLimitingRejectedBenchmarks`, each with its own baseline.

- [ ] **Step 1: Prove edge-limiter coverage is absent**

Run: `dotnet run -c Release --project perf/ApiPipeline.NET.Perf -- --filter '*EdgeRateLimiting*Benchmarks*' --job Dry`

Expected: no matching benchmark is executed.

- [ ] **Step 2: Implement the bounded allowed path**

Use `[InvocationCount(64), UnrollFactor(1)]`; iteration setup selects a fresh remote-IP partition with a permit limit above 64. Cleanup asserts every measured request invoked the terminal and returned below 400.

- [ ] **Step 3: Implement the exhausted rejected path and matching baseline**

Setup spends the low-limit partition once. The measured path returns 429 and never calls the terminal; its baseline uses identical context construction without rejecting middleware and is documented as a whole-request comparison.

- [ ] **Step 4: Run focused dry benchmarks**

Run: `dotnet run -c Release --project perf/ApiPipeline.NET.Perf -- --filter '*EdgeRateLimiting*Benchmarks*' --job Dry`

Expected: four successful rows with stable allowed/rejected cleanup counts.

---

### Task 5: Cold-start request-limit and version-deprecation measurements

**Files:**
- Modify: `perf/ApiPipeline.NET.Perf/ApiPipeline.NET.Perf.csproj:14-20`
- Create: `perf/ApiPipeline.NET.Perf/StartupConfigurationBenchmarks.cs`
- Update generated lock data: `perf/ApiPipeline.NET.Perf/packages.lock.json`

**Interfaces:**
- Produces: `RequestLimitsStartupBenchmarks.Baseline_FrameworkOptions()` and `RequestLimitsStartupBenchmarks.Package_RequestLimits()`.
- Produces: `VersionDeprecationStartupBenchmarks.Baseline_AspVersioning()` and `VersionDeprecationStartupBenchmarks.Package_DeprecationPolicies()`.
- Produces immutable result snapshots consumed by BenchmarkDotNet so option resolution cannot be eliminated.

- [ ] **Step 1: Prove startup coverage is absent**

Run: `dotnet run -c Release --project perf/ApiPipeline.NET.Perf -- --filter '*StartupBenchmarks*' '*StartupConfigurationBenchmarks*' --job Dry`

Expected: no matching benchmark is executed.

- [ ] **Step 2: Add the versioning project reference and restore**

Add `../../src/ApiPipeline.NET.Versioning/ApiPipeline.NET.Versioning.csproj`; run `dotnet restore perf/ApiPipeline.NET.Perf/ApiPipeline.NET.Perf.csproj` and retain the lock-file update.

- [ ] **Step 3: Implement request-limit cold-start baselines**

Each invocation builds and disposes a provider and resolves `KestrelServerOptions`. The active case binds `MaxRequestBodySize=1048576`, `MaxRequestHeadersTotalSize=32768`, `MaxRequestHeaderCount=64`, `RequestHeadersTimeout=00:00:15`, and `MaxConcurrentConnections=100`; validate the resolved snapshot.

- [ ] **Step 4: Implement version-deprecation cold-start baselines**

Both cases register Asp.Versioning. The active case registers versions `1.0`, `2.0`, and `3.0` with fixed deprecation/sunset dates and absolute links, resolves both package and `ApiVersioningOptions`, and validates all three bound entries and configured policy construction.

- [ ] **Step 5: Run focused dry benchmarks**

Run: `dotnet run -c Release --project perf/ApiPipeline.NET.Perf -- --filter '*RequestLimitsStartupBenchmarks*' '*VersionDeprecationStartupBenchmarks*' --job Dry`

Expected: four successful rows with the configured snapshots returned.

---

### Task 6: One-container live Redis benchmarks

**Files:**
- Modify: `perf/ApiPipeline.NET.Perf/ApiPipeline.NET.Perf.csproj:14-20`
- Modify: `perf/ApiPipeline.NET.Perf/Program.cs:1-3`
- Create: `perf/ApiPipeline.NET.Perf/PerfImages.cs`
- Create: `perf/ApiPipeline.NET.Perf/RedisBenchmarkEnvironment.cs`
- Create: `perf/ApiPipeline.NET.Perf/RedisRateLimitingBenchmarks.cs`
- Update generated lock data: `perf/ApiPipeline.NET.Perf/packages.lock.json`

**Interfaces:**
- Produces: `RedisBenchmarkEnvironment.ConnectionStringVariable` and `RedisBenchmarkEnvironment.RunAsync(string[], Action<string[]>) -> Task`.
- Produces: `RedisAllowedBenchmarks`, `RedisCachedRejectionBenchmarks`, and `RedisServerRejectionBenchmarks`.
- Consumes: public Redis package registration and the connection string inherited from the parent process.

- [ ] **Step 1: Prove Redis coverage is absent and Docker is available**

Run: `docker info`.

Expected: exit 0.

Run: `dotnet run -c Release --project perf/ApiPipeline.NET.Perf -- --filter '*Redis*Benchmarks*' --job Dry`

Expected: no matching benchmark is executed.

- [ ] **Step 2: Add Redis/Testcontainers dependencies and restore**

Add the Redis project reference and `Testcontainers.Redis` package reference; restore and retain the lock-file update.

- [ ] **Step 3: Start one required pinned container around BenchmarkDotNet**

Implement `RedisBenchmarkEnvironment.RunAsync`: start the digest-pinned Redis container, set `APIPIPELINE_PERF_REDIS_CONNECTION`, invoke the switcher, clear the variable, and dispose the container in `finally`. Convert Docker startup failure to an `InvalidOperationException` whose message states Docker is required. Update `Program.cs` to run the switcher through this method.

- [ ] **Step 4: Implement allowed Redis algorithms and the two-policy path**

Use real package pipelines for fixed window, sliding window, token bucket, concurrency, and global-plus-default. Warm connection/scripts in setup; bound each method's invocation count below its policy capacity; cleanup asserts every response stayed allowed.

- [ ] **Step 5: Implement and distinguish server and cached rejection**

For server rejection, iteration setup exhausts a fresh partition and one measured invocation gets Redis's denial. For cached rejection, setup obtains a denial once and measurement exercises the local deny cache. Before/after Redis command statistics must prove one call versus zero.

- [ ] **Step 6: Verify Docker failure behavior**

Run with an invalid Docker endpoint scoped only to the command, filtering to one Redis benchmark.

Expected: non-zero exit and an error containing `Docker is required` before any benchmark result is emitted.

- [ ] **Step 7: Run live Redis dry benchmarks**

Run: `dotnet run -c Release --project perf/ApiPipeline.NET.Perf -- --filter '*Redis*Benchmarks*' --job Dry`

Expected: ten successful rows, one container lifecycle, no `NA`, and command-count guards pass.

---

### Task 7: Complete-suite verification and performance documentation

**Files:**
- Modify: `docs/performance.md:3-94`

**Interfaces:**
- Consumes: all benchmark classes from Tasks 1-6.
- Produces: one documented command, complete coverage table, baseline/result tables, Docker prerequisites, and comparison guidance.

- [ ] **Step 1: Verify benchmark discovery**

Run: `dotnet run -c Release --project perf/ApiPipeline.NET.Perf -- --list flat`

Expected: all 16 existing rows plus 38 new rows are listed; Docker starts successfully.

- [ ] **Step 2: Run all relevant tests**

Run: `dotnet test tests/ApiPipeline.NET.Tests/ApiPipeline.NET.Tests.csproj -c Release --no-restore`

Run: `dotnet test tests/ApiPipeline.NET.RateLimiting.Redis.Tests/ApiPipeline.NET.RateLimiting.Redis.Tests.csproj -c Release --no-restore`

Expected: zero failures in both projects.

- [ ] **Step 3: Run the complete dry suite**

Run: `dotnet run -c Release --project perf/ApiPipeline.NET.Perf -- --filter '*' --job Dry --artifacts BenchmarkDotNet.Artifacts/dry`

Expected: 54 successful benchmark rows, no `NA`, no setup/cleanup failure, and one parent-managed Redis container.

- [ ] **Step 4: Run the allocation baseline suite**

Run: `dotnet run -c Release --project perf/ApiPipeline.NET.Perf -- --filter '*' --job short --artifacts BenchmarkDotNet.Artifacts/current`

Expected: reports for every benchmark class with latency, allocation, and GC columns.

- [ ] **Step 5: Update `docs/performance.md` from fresh artifacts**

Document Docker as mandatory, distinguish request-path versus cold-start costs, explain whole-request short-circuit ratios, record current environment, add every class/scenario, and state that local-container Redis latency is machine-specific. Record deterministic allocation values; do not present local Redis time as an industry threshold.

- [ ] **Step 6: Run final repository checks**

Run: `dotnet build ApiPipeline.NET.slnx -c Release --no-restore`

Run: `git diff --check`

Expected: build succeeds with zero errors and diff check reports nothing.

- [ ] **Step 7: Inspect only task-owned diffs**

Run: `git diff -- perf/ApiPipeline.NET.Perf docs/performance.md docs/superpowers/specs/2026-11-12-benchmark-feature-coverage-design.md docs/superpowers/plans/2026-11-12-benchmark-feature-coverage.md`

Expected: existing user edits remain intact; changes are limited to the approved benchmark coverage and documentation.
