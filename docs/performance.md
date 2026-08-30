# Performance Verification

BenchmarkDotNet microbenchmarks (allocation-gated in CI) and a k6 endpoint load test. Docker is
required: the suite starts a digest-pinned Redis 7.4 container.

## Running

```bash
# Allocations and a quick timing sample (what CI runs; --job Dry for a smoke check)
dotnet run -c Release --project perf/ApiPipeline.NET.Perf -- \
  --filter '*' --job short --artifacts BenchmarkDotNet.Artifacts/current

# Allocation gate
python3 -I scripts/check-benchmark-allocations.py \
  BenchmarkDotNet.Artifacts/current/results perf/ApiPipeline.NET.Perf/allocations-baseline.json

# Timing: five default-job runs on an idle machine, into .performance-local/
scripts/run-performance.sh

# Endpoint load test (--smoke for a 2-second check)
scripts/run-load-tests.sh
```

Request benchmarks invoke a built `RequestDelegate` on `DefaultHttpContext`; startup benchmarks
build and dispose a provider per operation; Redis benchmarks use the live container. Setup guards
assert each scenario's behaviour (cache hits skip the terminal, rejections return 429, one Redis
script per policy, zero for cached rejections, an open circuit for the per-pod fallback, one failed
Redis call per fail-closed rejection).

## Gates

- **Allocations** (pull requests and `main`): each row in `allocations-baseline.json` allows the
  larger of 64 B or 5% over the accepted value. Propose a change with `--write-candidate <path>` and
  review it; the accepted file is never overwritten by the tool. Accepted values come from Linux x64
  (what CI runs) on the SDK pinned in `global.json`; record them there, and keep each row's `reason`
  and any ceiling it explains. Rows measured process-wide (host startup, Redis reconnects) vary with
  architecture and background work, so their `reason` states a wider ceiling; every other row
  matches macOS Arm64 within the default tolerance.
- **Timing** is informational. A row is comparable only across five successful default-job runs with
  the same machine, toolchain and revision, and at most 5% CV. Promote a baseline explicitly with
  `summarize-benchmark-runs.py --promote`.
- **Load tests** block on functional checks only; throughput and latency are reported.

The Redis and k6 image digests (`perf/PerformanceImages.cs`, `perf/load/k6-image.txt`) are not
updated by Dependabot; update them with a smoke run.

## Results

Benchmarks: .NET 10.0.15, SDK 10.0.404, Apple M4, Docker 29.9.3, 2026-11-14, one Short-job run
on a desktop with other applications open: allocations are exact, timings indicative. Ratios
compare a deliberately tiny harness: read them as absolute added cost, not endpoint slowdown.

### Request path (Short job)

| Feature | Baseline | Feature | Ratio | Allocated |
| --- | ---: | ---: | ---: | ---: |
| Forwarded headers, none sent | 272 ns | 363 ns | 1.34× | 1.58 KB |
| Forwarded headers applied | 306 ns | 581 ns | 1.90× | 2.07 KB |
| CORS allowed GET | 277 ns | 608 ns | 2.20× | 2.59 KB |
| CORS preflight | 344 ns | 758 ns | 2.20× | 2.92 KB |
| Request timeout | 246 ns | 865 ns | 3.51× | 1.96 KB |
| Gzip 4 KB | 419 ns | 3.34 μs | 7.97× | 2.45 KB |
| Response cache hit | 410 ns | 842 ns | 2.05× | 2.73 KB |
| Output cache hit | 513 ns | 1.10 μs | 2.14× | 4.00 KB |
| Output cache miss | 5.68 μs | 25.9 μs | 4.55× | 84.86 KB |
| Edge limiter allowed | 1.16 μs | 1.80 μs | 1.56× | 1.76 KB |
| Edge limiter rejected | 260 ns | 746 ns | 2.87× | 2.99 KB |

| Middleware | Allocated over the 1 KB baseline |
| --- | ---: |
| Security headers | 0 B |
| Request size tracking | ~250 B |
| Correlation ID (generated / inbound) | ~460 B / ~580 B |
| Rate limiter lease | 2.20 KB total |
| Full pipeline at 1 / 8 / 64 concurrent | 3.00 / 2.90 / 2.88 KB per request |

The output-cache miss allocation (~81 KB plus the payload) is ASP.NET Core's response capture and
serialization: a non-retaining store saves only 256 B. It is a documented exception in the baseline.

### Startup (per provider build)

| Scenario | Baseline | Package | Allocated |
| --- | ---: | ---: | ---: |
| Request limits | 2.60 μs | 15.7 μs | 37.40 KB |
| Three deprecation policies | 6.85 μs | 18.4 μs | 44.43 KB |

### Redis (local Docker; not a deployment SLO)

| Scenario | Mean | Scripts per request |
| --- | ---: | ---: |
| Fixed / sliding / token bucket allowed | 203–242 μs | 1 |
| Concurrency allowed | 266 μs | 1 (+ release) |
| Global plus default | 274 μs | 2 |
| Server-side rejection | 144 μs | 1 |
| Locally cached rejection | 23 μs | 0 |
| Redis unreachable, circuit open (`PerPod`) | 5.9 μs | 0 |
| Same, 64 requests at once | 207 μs per 64 | 0 |
| Redis unreachable, fail-closed (`Reject`) | 51.3 ms with `CommandTimeout` 50 ms | 1 (fails) |

The outage rows use a closed loopback port. A fail-closed policy bypasses the circuit, so each of
its requests waits up to `CommandTimeout` while Redis is down: size `CommandTimeout` for that
latency. Its allocation row has a wide ceiling, since the measurement includes the client's
background reconnects.

Five-run timing (.NET 10.0.14, SDK 10.0.403, Apple M4, 2026-11-12/13; not yet repeated on the
current SDK): 39 of 63 rows met the 5% CV bar; cache misses, some Redis paths, CORS GET and 8/64-way
concurrency did not. No timing baseline is promoted yet.

### Endpoint load (16 VUs, 1 KB JSON, local Docker)

.NET 10.0.14, SDK 10.0.403, Apple M4, 2026-11-12/13, on an idle machine; not yet repeated on the
current SDK. A load run is comparable only on an idle machine: background load moves throughput
and p95 far more than the pipeline does.

| Scenario | Throughput | p50 | p95 | p99 |
| --- | ---: | ---: | ---: | ---: |
| Baseline | 26,017 req/s | 0.50 ms | 0.87 ms | 1.47 ms |
| Full pipeline | 24,608 req/s | 0.54 ms | 0.93 ms | 1.59 ms |
| Redis-backed pipeline | 16,632 req/s | 0.85 ms | 1.27 ms | 2.00 ms |
| Output-cache hit | 23,195 req/s | 0.56 ms | 1.01 ms | 1.87 ms |
| Output-cache miss (50 req/s) | 50 req/s | 1.30 ms | 2.40 ms | 4.97 ms |
| Redis rejection (1 VU) | 5,040 req/s | 0.16 ms | 0.26 ms | 0.43 ms |

Full-pipeline p95 is 7% over baseline (target ≤ 20%). Measure Redis in your real topology before
setting SLOs.

## Comparing changes

Same SDK, runtime, machine and command before and after; compare allocations first; compare timing
only from stable default jobs; validate saved work under load. See
[ASP.NET Core performance best practices](https://learn.microsoft.com/aspnet/core/performance/performance-best-practices).
