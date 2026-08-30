# Performance Hardening Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a repeatable local/CI performance program that attributes output-cache miss allocation, gates allocation regressions, aggregates five stable BenchmarkDotNet runs, and load-tests Kestrel with local Docker Redis.

**Architecture:** Python tools parse BenchmarkDotNet CSV into one typed row model used by the allocation gate and timing summarizer. BenchmarkDotNet remains the microbenchmark authority; a separate non-shipping Kestrel host and digest-pinned k6 container provide endpoint throughput and percentile evidence. Pull requests gate allocations only, while timing and load artifacts remain informational unless a five-run same-machine baseline is explicitly promoted.

**Tech Stack:** .NET 10, ASP.NET Core, BenchmarkDotNet 0.15.8, Testcontainers.Redis, Python 3 standard library, Bash, k6 in Docker, GitHub Actions.

**Spec:** `docs/superpowers/specs/2026-11-12-performance-hardening-design.md`

## Global Constraints

- Docker is mandatory; Redis uses the repository's digest-pinned Redis 7.4 image.
- Local Docker is a reproducible development topology, not a production-latency claim.
- CI gates managed allocation only; timing and throughput on GitHub-hosted runners are informational.
- Timing eligibility requires exactly five successful runs, one matching fingerprint, and cross-run CV at most 5%; the default regression allowance is 10%.
- The canonical endpoint load is 16 VUs, 15 seconds warmup, 60 seconds measured, and a fixed 1 KB JSON response; cache misses are capped at 50 requests/second.
- The output-cache investigation attributes and documents ownership; production optimization is out of scope without separate approval.
- Never write Redis credentials or connection strings to logs or artifacts; refuse non-loopback remote Redis.
- Preserve all unrelated staged, unstaged, and untracked user changes in the native `main` checkout.
- Commit steps are checkpoints in the plan; do not execute them in the shared dirty checkout without explicit user authorization.

## Review Focus

- Benchmark CSV with a localized/unknown unit or `NA` allocation must fail closed rather than become zero — pinned in Task 1 parser tests.
- Parameterized benchmark rows must have distinct stable keys, while duplicate identical keys must fail — pinned in Task 1 parser tests.
- An allocation result missing one checked-in baseline row or introducing an unreviewed row must fail — pinned in Task 2 gate tests.
- Mixed machine fingerprints, four-run inputs, and CV above 5% must be reported ineligible and never treated as a pass — pinned in Task 4 summarizer tests.
- Load orchestration interrupted during startup or k6 execution must stop the host/container and leave no Redis container running — pinned in Task 6 interruption smoke test.

---

### Task 1: Shared BenchmarkDotNet result parser

**Files:**
- Create: `scripts/performance/__init__.py`
- Create: `scripts/performance/benchmark_results.py`
- Create: `scripts/tests/test_benchmark_results.py`
- Create: `scripts/tests/fixtures/benchmark-report.csv`

**Interfaces:**
- Consumes: BenchmarkDotNet `*-report.csv` files.
- Produces: `BenchmarkRow`, `parse_reports(path: Path) -> list[BenchmarkRow]`, `parse_measurement(value: str, quantity: Literal["time", "bytes"]) -> float`, and `row_key(class_name: str, method: str, parameters: Mapping[str, str]) -> str`.

- [ ] **Step 1: Write failing parser tests**

Cover B/KB/MB and ns/µs/ms conversion, class name from the report filename, parameter-order-independent keys, `NA`, unknown units, missing columns, and duplicate row keys. Assert invalid input raises `BenchmarkResultError` with the source file and row.

- [ ] **Step 2: Run the parser tests and verify failure**

Run: `python3 -m unittest scripts.tests.test_benchmark_results -v`

Expected: FAIL because `scripts.performance.benchmark_results` does not exist.

- [ ] **Step 3: Implement the parser model and functions**

`BenchmarkRow` is an immutable dataclass containing `key`, `class_name`, `method`, `parameters`, `mean_ns`, `allocated_bytes`, and `source`. Treat empty allocation as an error for allocation-gated reports. Sort parameter names when building `Class.Method[name=value,...]` keys.
For BenchmarkDotNet 0.15.8 CSV, treat custom columns between `WarmupCount` and `Mean` as benchmark parameters; the fixed job-characteristic columns and metric columns never enter the row key.

- [ ] **Step 4: Run parser tests**

Run: `python3 -m unittest scripts.tests.test_benchmark_results -v`

Expected: all tests pass.

- [ ] **Step 5: Commit checkpoint**

Commit message: `test(perf): add benchmark result parser`

### Task 2: Allocation regression gate and reviewed baseline

**Files:**
- Create: `scripts/check-benchmark-allocations.py`
- Create: `scripts/performance/allocation_gate.py`
- Create: `scripts/tests/test_allocation_gate.py`
- Create: `perf/ApiPipeline.NET.Perf/allocations-baseline.json`
- Modify: `docs/performance.md`

**Interfaces:**
- Consumes: `parse_reports`, current BenchmarkDotNet result directory, and allocation baseline schema version 1.
- Produces: `check_allocations(rows: Sequence[BenchmarkRow], baseline: AllocationBaseline) -> AllocationReport`; CLI options `RESULTS BASELINE`, `--write-candidate PATH`, and exit 0/1.

- [ ] **Step 1: Write failing allocation-gate tests**

Assert exact pass, the larger-of-64-bytes-or-5%-allowance calculation, over-budget failure, missing row failure, unexpected row failure, duplicate failure propagated from the parser, and candidate output that never overwrites the input baseline.

- [ ] **Step 2: Run allocation-gate tests and verify failure**

Run: `python3 -m unittest scripts.tests.test_allocation_gate -v`

Expected: FAIL because `allocation_gate` does not exist.

- [ ] **Step 3: Implement baseline schema and gate**

Schema 1 contains `sourceEnvironment`, `runtime`, and `rows`; every row stores `acceptedBytes`, `maximumBytes`, and optional `reason`. The CLI prints key, accepted, current, byte delta, percent delta, and status. It refuses to mutate the accepted baseline.

- [ ] **Step 4: Generate and review the candidate baseline**

Run the complete `Short` suite into a fresh artifact directory, generate a candidate, inspect every unexpected/missing row, then copy the reviewed candidate to `allocations-baseline.json`. Give the output-cache miss an explicit framework-investigation reason rather than a silent wide tolerance.

- [ ] **Step 5: Prove pass and failure behavior**

Run the gate against the fresh reports and expect exit 0. Run it against a test fixture with one allocation above maximum and expect exit 1 with that key named.

- [ ] **Step 6: Document allocation baseline review**

Explain why allocation is gated across supported 64-bit .NET 10 environments, how to generate a candidate, and why baseline promotion requires code review.

- [ ] **Step 7: Commit checkpoint**

Commit message: `ci(perf): gate benchmark allocations`

### Task 3: Attribute output-cache miss allocation

**Files:**
- Create: `perf/ApiPipeline.NET.Perf/OutputCachingAllocationBenchmarks.cs`
- Modify: `perf/ApiPipeline.NET.Perf/CachingBenchmarks.cs`
- Modify: `docs/performance.md`

**Interfaces:**
- Consumes: `CacheBenchmarkPipeline`, `BenchmarkPipeline.Require`, ASP.NET Core `IOutputCacheStore`.
- Produces: `CountingOutputCacheStore : IOutputCacheStore` and `OutputCachingAllocationBenchmarks` with non-cacheable, instrumented-store, and default-store rows parameterized by 23, 1,024, and 65,536-byte payloads.

- [ ] **Step 1: Add semantic guards before measurement rows**

Write setup assertions that the non-cacheable row performs zero store writes, the instrumented row performs exactly one `SetAsync`, the default-store row produces a subsequent hit, and every miss invokes the terminal once. Run a focused Dry job and verify it fails before the diagnostic pipelines exist.

- [ ] **Step 2: Implement `CountingOutputCacheStore`**

Implement `GetAsync(string, CancellationToken)`, `SetAsync(string, byte[], string[], TimeSpan, CancellationToken)`, and `EvictByTagAsync(string, CancellationToken)`. Record call counts and stored byte lengths without retaining the value after the call.

- [ ] **Step 3: Implement parameterized diagnostic pipelines**

Reuse the same `DefaultHttpContext` and `CountingWriteStream` construction as the existing miss row. Generate payload buffers once in `GlobalSetup`; timed terminals only write the selected payload. Replace only `IOutputCacheStore` for the instrumented-store pipeline.

- [ ] **Step 4: Run focused Dry and Short jobs**

Expected: every semantic guard passes; reports contain all payload/mode combinations with non-NA allocations.

- [ ] **Step 5: Inspect .NET 10 source and optional allocation trace**

Correlate the fixed/scaled allocation pattern with `OutputCacheMiddleware`/response-body capture/default-store source. If `dotnet-trace` is available, collect GC allocation ticks for the focused benchmark; absence of the optional tool is documented, not hidden.

- [ ] **Step 6: Record the attribution**

Update `docs/performance.md` with the measured fixed and payload-scaled components, the responsible layer, and whether the existing output-cache miss ceiling is a justified framework exception or a package-owned follow-up proposal. Do not change shipped behavior.

- [ ] **Step 7: Refresh the allocation baseline**

Generate a candidate containing the new diagnostic rows, review each ceiling, and rerun Task 2's gate.

- [ ] **Step 8: Commit checkpoint**

Commit message: `perf: attribute output cache miss allocations`

### Task 4: Five-run timing orchestration and stability summary

**Files:**
- Create: `scripts/run-performance.sh`
- Create: `scripts/summarize-benchmark-runs.py`
- Create: `scripts/performance/timing_summary.py`
- Create: `scripts/tests/test_timing_summary.py`
- Modify: `.gitignore`

**Interfaces:**
- Consumes: Task 1 rows, five artifact directories, `APIPIPELINE_PERF_HOME`.
- Produces: `MachineFingerprint`, `TimingSummary`, `summarize_runs(...)`, CLI `--baseline`, `--candidate`, `--promote`, `--max-cv 0.05`, `--max-regression 0.10`, and local layout `candidates/` plus `accepted/`.

- [ ] **Step 1: Write failing stability tests**

Assert five identical fingerprints with CV ≤5% are eligible; four runs, a fingerprint mismatch, a missing row, and CV >5% are ineligible; a >10% eligible regression fails only when enforcement is explicitly requested; promotion refuses a fingerprint mismatch and never overwrites without `--promote`.

- [ ] **Step 2: Run timing tests and verify failure**

Run: `python3 -m unittest scripts.tests.test_timing_summary -v`

Expected: FAIL because `timing_summary` does not exist.

- [ ] **Step 3: Implement fingerprint and summary calculations**

Fingerprint fields are OS, architecture, CPU model, .NET SDK/runtime, Docker server version, and benchmark git revision. Use the median of run means and population CV across the five run means. Output JSON plus Markdown.

- [ ] **Step 4: Implement the local runner**

Default to five sequential runs with no `--job` override, a fresh timestamped candidate directory, Docker preflight, Release build, stop-on-first-error, and traps that restore environment state. Support `--runs` for smoke testing but mark fewer than five ineligible.

- [ ] **Step 5: Add local artifacts to `.gitignore`**

Ignore `.performance-local/` without ignoring checked-in allocation baselines or documentation.

- [ ] **Step 6: Smoke-test orchestration**

Run one Dry repetition through the same artifact/fingerprint path and verify the summarizer reports it ineligible because it has fewer than five runs.

- [ ] **Step 7: Commit checkpoint**

Commit message: `perf: add stable repeated benchmark runner`

### Task 5: Endpoint load-test host with baseline, pipeline, and Redis profiles

**Files:**
- Create: `perf/PerformanceImages.cs`
- Move/Modify: `perf/ApiPipeline.NET.Perf/PerfImages.cs`
- Modify: `perf/ApiPipeline.NET.Perf/ApiPipeline.NET.Perf.csproj`
- Create: `perf/ApiPipeline.NET.LoadTests/ApiPipeline.NET.LoadTests.csproj`
- Create: `perf/ApiPipeline.NET.LoadTests/Program.cs`
- Create: `perf/ApiPipeline.NET.LoadTests/LoadTestProfile.cs`
- Create: `perf/ApiPipeline.NET.LoadTests/LoadTestPayload.cs`
- Create: `scripts/smoke-load-test-host.sh`
- Modify: `ApiPipeline.NET.slnx`

**Interfaces:**
- Consumes: shared `PerformanceImages.Redis`, ApiPipeline registration APIs, Redis add-on, profile argument `baseline|pipeline|redis`.
- Produces: loopback Kestrel host with `/health`, `/payload`, `/cached`, `/reject`, profile header `X-ApiPipeline-Load-Profile`, and Redis lifecycle owned by the `redis` profile.

- [ ] **Step 1: Write the failing host smoke test**

The script starts each profile on an available loopback port, polls `/health`, verifies the profile header and 1 KB `/payload`, verifies two `/cached` requests are successful, and verifies an invalid profile exits nonzero without starting Kestrel.

- [ ] **Step 2: Run smoke test and verify failure**

Expected: FAIL because the load-test host project does not exist.

- [ ] **Step 3: Share the Redis image constant**

Move the digest-pinned constant into `perf/PerformanceImages.cs` under namespace `ApiPipeline.NET.Performance`; link it into both perf projects and update the microbenchmark reference. Build to prove the digest has one source of truth.

- [ ] **Step 4: Implement profile parsing and baseline host**

`LoadTestProfile.Parse(string)` accepts exactly `baseline`, `pipeline`, or `redis`. The baseline profile maps the endpoints without ApiPipeline services or middleware.

- [ ] **Step 5: Implement pipeline profile**

Register a deterministic in-memory configuration with the representative pipeline, permissive rate limits, output caching, response compression, timeouts, correlation, security headers, request sizing, and CORS. Map a fixed 1 KB JSON payload, opt `/cached` into output caching, and map `/reject` to a separate named policy whose low permit limit is used only by the rejection scenario.

- [ ] **Step 6: Implement Redis profile and safety**

Start the shared digest-pinned Testcontainers Redis container, configure a unique key prefix and non-secret 32-character test partition secret, replace the in-memory limiter through the public Redis registration API, and dispose the container on shutdown. Reject any externally supplied non-loopback Redis endpoint.

- [ ] **Step 7: Run host smoke test**

Expected: all profiles pass; after the Redis profile exits, `docker ps` shows no container bearing the run label.

- [ ] **Step 8: Commit checkpoint**

Commit message: `perf: add endpoint load test host`

### Task 6: Digest-pinned k6 scenarios and load reports

**Files:**
- Create: `perf/load/k6/scenarios.js`
- Create: `perf/load/k6/summary.js`
- Create: `perf/load/k6-image.txt`
- Create: `scripts/run-load-tests.sh`
- Create: `scripts/performance/load_summary.py`
- Create: `scripts/tests/test_load_summary.py`
- Modify: `scripts/smoke-load-test-host.sh`

**Interfaces:**
- Consumes: Task 5 host, canonical profile/scenario values, Docker.
- Produces: per-scenario k6 JSON, aggregate Markdown/JSON containing requests/sec, median, p95, p99, failures, checks, metadata, and baseline-to-pipeline p95 delta.

- [ ] **Step 1: Write failing load-summary tests**

Assert required metric extraction, missing percentile failure, unexpected status/check failure, profile mismatch failure, and exact p95 delta calculation. Add an interruption test whose fake long-running host/container receives SIGTERM and is cleaned up by the orchestration trap.

- [ ] **Step 2: Run tests and verify failure**

Run: `python3 -m unittest scripts.tests.test_load_summary -v`

Expected: FAIL because `load_summary` does not exist.

- [ ] **Step 3: Implement k6 scenarios**

Pin `perf/load/k6-image.txt` to `grafana/k6@sha256:3d8e5c7f1d53ec59408c2ffbd9943602a0985ffcbd4a2430ec00f3ffca89796e`. Use 16 VUs, 15-second warmup, and 60-second measurement for baseline, pipeline, Redis, and cache-hit runs. Use constant-arrival-rate 50 requests/second for bounded unique-key cache misses. Add a short `/reject` Redis scenario that first consumes its named policy and then expects 429. Every request checks status and `X-ApiPipeline-Load-Profile`.

- [ ] **Step 4: Implement deterministic summaries**

Have `handleSummary` write raw k6 summary JSON. The Python aggregator adds environment metadata and produces the compact table; functional failures exit 1, timing values never cause exit 1 by default.

- [ ] **Step 5: Implement orchestration and cleanup**

Run the host directly with `dotnet`, run the digest-pinned k6 image with `host.docker.internal`, add Linux host-gateway mapping when needed, and trap EXIT/INT/TERM to stop child processes and containers. Never print connection strings.

- [ ] **Step 6: Run a shortened smoke campaign**

Override durations only for smoke testing. Verify all scenario metrics exist, the rejection scenario contains only expected 429s after warmup, and cleanup leaves no labeled containers.

- [ ] **Step 7: Commit checkpoint**

Commit message: `perf: add Kestrel load scenarios`

### Task 7: Performance CI workflow and documentation

**Files:**
- Create: `.github/workflows/performance.yml`
- Modify: `.github/dependabot.yml`
- Modify: `docs/performance.md`
- Modify: `CONTRIBUTING.md`

**Interfaces:**
- Consumes: Tasks 2, 4, and 6 commands and artifacts.
- Produces: blocking allocation job on PR/main; scheduled/manual non-blocking repeated timing and endpoint load jobs; documented maintainer workflow.

- [ ] **Step 1: Add allocation CI job**

Trigger on pull requests and pushes to `main`. Use the same private-feed authentication and pinned setup/upload action SHAs as `ci.yml`, locked restore, Release build, complete `Short` suite with Docker, allocation gate, unconditional artifact upload, and a 45-minute timeout. Timing columns are ignored.

- [ ] **Step 2: Add scheduled/manual timing job**

Trigger weekly at `0 5 * * 0` UTC and by manual dispatch. Use a 240-minute timeout, run five default repetitions through Task 4, upload raw and summary artifacts, and use `continue-on-error` only for the timing comparison/reporting step—not build, benchmark semantic failures, or artifact generation.

- [ ] **Step 3: Add scheduled/manual load job**

On the same schedule/manual trigger, use a 45-minute timeout, run Task 6's canonical campaign, fail on functional/check errors, publish throughput/percentiles, and never gate on timing values.

- [ ] **Step 4: Extend Dependabot coverage**

Ensure the new workflow's action SHAs and Docker image review process are covered by existing repository policy or explicitly documented where Dependabot cannot update a digest.

- [ ] **Step 5: Document maintainer commands and evidence rules**

Update performance and contributing docs with allocation candidate review, five-run promotion, same-fingerprint comparison, Docker-only Redis limitation, canonical load profile, artifact locations, and the distinction between functional and timing failures.

- [ ] **Step 6: Validate workflow and docs**

Run Python tool tests, Markdown lint, link checker, locked restore, and inspect workflow syntax with the repository's available YAML validation path. Expected: zero failures.

- [ ] **Step 7: Commit checkpoint**

Commit message: `ci(perf): publish allocation and load evidence`

### Task 8: Execute campaigns and final verification

**Files:**
- Modify: `docs/performance.md`
- Modify: `perf/ApiPipeline.NET.Perf/allocations-baseline.json` only if fresh reviewed evidence requires it.

**Interfaces:**
- Consumes: the complete performance toolchain from Tasks 1-7.
- Produces: fresh allocation gate evidence, five-run default-job summary, endpoint load summary, and final repository verification.

- [ ] **Step 1: Run all Python tool tests**

Run: `python3 -m unittest discover -s scripts/tests -v`

Expected: all tests pass.

- [ ] **Step 2: Run complete allocation suite and gate**

Run the complete `Short` suite in a fresh directory, then run `check-benchmark-allocations.py`. Expected: every discovered row is accounted for and within its reviewed maximum.

- [ ] **Step 3: Run five default BenchmarkDotNet repetitions**

Use `scripts/run-performance.sh` with default settings. Expected: five successful artifact sets, one fingerprint, no semantic failure, and a summary that clearly labels stable versus unstable rows. Do not promote automatically.

- [ ] **Step 4: Run canonical endpoint load campaign**

Use `scripts/run-load-tests.sh`. Expected: baseline, pipeline, Redis, cache hit/miss, and rejection reports with throughput, median, p95, p99, zero unexpected statuses, and complete cleanup.

- [ ] **Step 5: Update current evidence**

Record the date/environment, output-cache attribution, allocation-gate result, stable timing summary, load-test table, and local-Docker caveat in `docs/performance.md`.

- [ ] **Step 6: Run full repository verification**

Run the Release solution build, complete test suite including Docker Redis tests, benchmark Dry discovery, Markdown lint, documentation link checker, and `git diff --check`. Expected: all pass with zero warnings/errors relevant to the change.

- [ ] **Step 7: Inspect task-owned diffs only**

Confirm unrelated user changes remain untouched and no raw performance artifacts, connection strings, or secrets are tracked.

- [ ] **Step 8: Commit checkpoint**

Commit message: `docs(perf): record hardened performance baseline`
