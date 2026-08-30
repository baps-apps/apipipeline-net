# Performance hardening and regression gates — design

## Intent

Turn the new BenchmarkDotNet coverage into a repeatable performance program: explain the large
output-cache miss allocation, collect stable repeated timing evidence locally, exercise real Kestrel
endpoints under load, measure Redis through the only available topology (a pinned local Docker
container), and prevent allocation regressions in pull requests without treating noisy shared-runner
timings as release gates.

The work is for maintainers deciding whether a package change is safe to merge. It must separate
deterministic evidence (semantics and managed allocation) from environment-sensitive evidence
(latency, throughput, and percentiles).

## Goals

- Attribute the approximately 84.82 KB allocated by an output-cache miss to the framework capture
  path, the default memory store, the benchmark harness, or package-owned code before attempting an
  optimization.
- Provide one local command that performs five complete default BenchmarkDotNet runs in an isolated
  session and produces a cross-run stability summary.
- Add endpoint-level load tests that report requests/second, median, p95, p99, failure rate, and
  status-code correctness for a framework baseline, the full in-process pipeline, output-cache hit
  and miss traffic, and Redis-backed limiting.
- Use the digest-pinned local Docker Redis topology for both microbenchmarks and endpoint tests.
- Gate managed allocation regressions on every pull request.
- Publish timing and load-test reports from shared CI without failing a change on those timings.
- Permit a timing comparison only when it contains five runs from the same machine fingerprint and
  the cross-run coefficient of variation is within the configured stability bound.

## Non-goals

- Claim that local Docker Redis represents production Redis latency.
- Run tests against a live production or shared Redis instance.
- Create a hard timing gate on a GitHub-hosted runner.
- Replace BenchmarkDotNet with the endpoint load test, or treat microbenchmarks as throughput tests.
- Change package behavior merely to make a benchmark smaller.
- Optimize framework-owned output-cache allocations unless the investigation proves an actionable
  package-owned cause.

## Constraints and safety

- Docker remains mandatory. Redis uses the repository's reviewed Redis 7.4 image digest.
- There is no dedicated machine or remote Redis service. A maintainer's workstation, used during an
  otherwise idle performance session, is the authoritative timing environment.
- GitHub-hosted runners are useful for allocation gates and artifacts, but their timing is
  informational only.
- Redis keys use a unique run prefix and a fixed non-secret test partition secret. The load harness
  refuses a non-loopback Redis endpoint unless a future design explicitly adds remote support.
- No credentials belong in scripts, configuration files, benchmark artifacts, logs, or chat.
- Existing user changes in the native `main` checkout must remain intact.

## Approaches considered

### 1. Local-authoritative timing plus CI allocation gates and informational reports — selected

Run five default jobs locally, retain every raw report, and aggregate only matching machine
fingerprints. In CI, run the shorter allocation suite, fail on allocation budgets, and upload timing
data without enforcing it. Run endpoint tests against Kestrel and Docker Redis locally and on a
scheduled/manual shared runner, but enforce only functional correctness there.

This matches the available infrastructure and makes the trustworthy signals blocking while keeping
noisy signals visible.

### 2. GitHub-hosted timing as the authority

This needs no maintainer workstation workflow, but VM generation, co-tenancy, power management, and
CPU variation make small middleware timing regressions indistinguishable from runner noise. It is
retained only as a non-blocking trend source.

### 3. Fully containerized performance appliance

Putting the application, Redis, and load generator in containers improves orchestration, but does
not remove host scheduling noise and makes BenchmarkDotNet CPU control less representative. The
selected design containers Redis and the load generator while running .NET directly on the host or
runner.

## Output-cache allocation investigation

The current miss writes only `cache benchmark payload`, yet allocates about 84.82 KB. The
investigation must establish ownership rather than infer it from the total.

Add focused diagnostic rows adjacent to `OutputCachingMissBenchmarks` that keep the same context and
terminal but separate:

1. middleware pass-through for a response explicitly marked non-cacheable;
2. cacheable response capture using a minimal instrumented `IOutputCacheStore`;
3. cacheable response capture using ASP.NET Core's default memory store;
4. small and larger payload sizes to distinguish fixed buffer rental/allocation from payload-scaled
   storage.

Every row has semantic guards proving whether the terminal ran and whether a store write occurred.
Inspect the matching .NET 10 ASP.NET Core source path and, when available, collect an EventPipe GC
allocation trace for the focused process. The result is a short evidence note in
`docs/performance.md` that names the responsible layer. If the excess is framework-owned and fixed,
record a justified budget exception. If package-owned work is material, a separate TDD patch may
remove it and must preserve public behavior.

## Allocation regression gate

Check in `perf/ApiPipeline.NET.Perf/allocations-baseline.json`, keyed by benchmark class, method, and
parameter values. Store bytes per operation, not formatted KB strings. Each row has an explicit
maximum derived from the accepted baseline; the default allowance is the larger of 64 bytes or 5%,
with intentionally large or framework-owned rows documented individually.

Add `scripts/check-benchmark-allocations.py` to:

- read all BenchmarkDotNet CSV reports in a supplied result directory;
- normalize B/KB/MB allocation values to bytes;
- construct stable row keys including parameters;
- fail on missing expected rows, unexpected duplicate rows, unparsable/NA allocations, or values
  above their maximum;
- print a compact old/current/delta table; and
- support an explicit baseline-update mode that writes a candidate file for review rather than
  silently relaxing a gate.

A single checked-in ceiling set applies to supported 64-bit .NET 10 targets. Its metadata records
the Apple Arm64 run that established it, but the ceilings deliberately include the 5%/64-byte
allowance before Ubuntu x64 CI evaluates them. A platform difference does not silently create a new
profile: a maintainer must inspect the CI artifact and explicitly update the reviewed ceiling if the
managed object layout is legitimately different. This keeps the first CI run blocking without
pretending that raw timing is portable.

A new CI performance job runs the complete `Short` allocation suite with Docker, checks the baseline,
and uploads raw reports. Timings from this job are never evaluated by the gate.

## Repeated BenchmarkDotNet timing

Add a local orchestration script with defaults of five runs and the BenchmarkDotNet default job. It
captures SDK, the runtime actually executing the built benchmark host, BenchmarkDotNet job, OS,
architecture, CPU, Docker, and git revision into a machine fingerprint, uses a fresh artifact
directory per run, stops on any semantic failure, and never overwrites the accepted baseline.

Add a summarizer that reports, for every row:

- all per-run means;
- median of the run means;
- minimum and maximum;
- cross-run standard deviation and coefficient of variation; and
- allocation min/max as a secondary consistency check.

A timing comparison is eligible to fail only when there are exactly five successful Default-job
runs with the same fingerprint, the benchmark exists in all runs, and coefficient of variation is
at most 5%. Non-default jobs are never promotable, and enforced comparisons fail closed when either
side is ineligible.
The default regression allowance is 10% against a baseline captured on the same fingerprint.
Ineligible rows are reported as unstable, never passed or failed. Shared CI invokes the same runner
on schedule or manually, uploads its summary, and remains non-blocking for timing.

Local candidates and accepted timing baselines live under a gitignored performance home selected by
`APIPIPELINE_PERF_HOME` and defaulting to `.performance-local/` in the repository. The runner writes
new candidates under `candidates/<timestamp>-<fingerprint>` and compares with
`accepted/<fingerprint>`. Promotion is a separate explicit `--promote <candidate>` command that
refuses a fingerprint mismatch and never runs implicitly after a successful benchmark.

## Endpoint load-test host

Add a small non-shipping project under `perf/` with explicit profiles selected at startup:

- `baseline`: Kestrel endpoint and fixed JSON payload without ApiPipeline middleware;
- `pipeline`: the representative full in-process pipeline with permissive rate limits;
- `redis`: the same pipeline with Redis-backed rate limiting and a unique key prefix.

The host exposes:

- `/health` for readiness;
- `/payload` for normal uncached traffic;
- `/cached` for a warmed output-cache hit;
- `/cached?key=<unique>` for bounded miss traffic; and
- `/reject` with a deliberately small named Redis policy for 429 validation; and
- a response header identifying the selected profile so the load script cannot compare the wrong
  process.

The host binds loopback only. The Redis profile accepts only the connection string supplied by the
local orchestration process, uses a high enough permit limit not to reject the normal load scenario,
and exposes a separate deliberate rejection scenario.

## Load generation and reports

Use `grafana/k6@sha256:3d8e5c7f1d53ec59408c2ffbd9943602a0985ffcbd4a2430ec00f3ffca89796e`
(the official multi-architecture `latest` manifest observed on 2026-11-12) so maintainers do not
install a global load tool. The
orchestration script starts one host profile at a time, waits for readiness, runs a short warmup, then
runs the measured scenario. On Linux it adds the documented host-gateway mapping; on Docker Desktop
it uses `host.docker.internal`.

Each scenario records:

- `http_reqs` rate (throughput);
- `http_req_duration` median, p95, and p99;
- request failure/check rate;
- expected status-code distribution; and
- scenario, profile, runtime, CPU, Docker, and git metadata.

The first canonical workload uses 16 virtual users, a 15-second warmup, and a 60-second measured
interval with a fixed 1 KB JSON response. Baseline, full-pipeline, Redis, cache-hit, and cache-miss
are separate runs rather than a mixed request stream. Cache hits use one warmed key. Cache misses use
a unique bounded key per iteration and a 50 requests/second arrival rate so the store cannot grow
without bound. The Redis policy limit is sized above the expected run volume; a separate short
scenario deliberately exhausts a partition and validates 429 responses.

Functional thresholds are blocking: readiness must succeed, checks must be 100%, unexpected status
codes must be zero, and the process must exit cleanly. Latency and throughput thresholds are report
only on shared CI. For local comparison, the report highlights the full-pipeline p95 delta from the
baseline and the existing project target of no more than 20%, but it does not fail until a maintainer
has accepted a five-run baseline for that fingerprint.

## CI and documentation

Add a separate performance workflow so normal build/test feedback stays focused:

- pull requests and `main`: complete `Short` suite, allocation gate, raw artifact upload;
- schedule and manual dispatch: five default BenchmarkDotNet runs plus endpoint load tests, summary
  upload, no timing failure;
- concurrency cancellation prevents two performance jobs competing on one runner.

Update `docs/performance.md` with the commands, artifact layout, allocation-baseline review process,
local Docker limitation, stability rules, load scenarios, and the output-cache allocation finding.
Document that allocations are merge gates while timing becomes a gate only on a stable same-machine
five-run baseline.

The output-cache work ends with attribution and documentation. If it proves a material
package-owned allocation, this project records the evidence and proposed acceptance test, then stops;
changing shipped behavior requires separate approval and a focused TDD patch.

## Verification

- Unit-test the allocation parser, row-key construction, unit conversion, missing-row behavior, and
  threshold failures with small fixture CSV files.
- Unit-test the repeated-run summarizer's fingerprint, CV, eligibility, and regression decisions.
- Run focused output-cache diagnostics and preserve the attribution evidence.
- Run the complete allocation suite and prove the checked baseline passes, then perturb a fixture to
  prove the gate fails.
- Run all endpoint scenarios against Kestrel and local Docker Redis and verify JSON/Markdown reports
  contain throughput, median, p95, and p99.
- Run at least one complete local five-run default-job campaign; keep the raw artifacts outside git
  and commit only its concise environment/result summary.
- Run the repository's full Release build and test suite, Markdown/link checks, and `git diff --check`.
