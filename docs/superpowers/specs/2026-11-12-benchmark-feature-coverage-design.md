# Complete Feature Benchmark Coverage Design

## Intent

Extend `perf/ApiPipeline.NET.Perf` so the documented performance picture covers every package
area currently called out as unmeasured: forwarded headers, CORS, request timeouts, response
compression, response caching, output caching, edge rate limiting, request-limit startup
configuration, version-deprecation startup configuration, and Redis-backed rate limiting.

The suite exists to answer two separate questions without mixing their costs:

1. What steady-state time and managed allocation does an enabled request-path feature add?
2. What one-time startup cost does a configuration-only feature add?

Redis measurements must use a real Redis server. Docker is a hard prerequisite for the complete
suite; missing Docker is a benchmark failure, not a skip.

## Goals

- Give each feature a paired baseline that performs the same harness and terminal work without the
  feature.
- Exercise the active behavior, not merely install middleware that becomes a no-op.
- Report latency, managed bytes per operation, and GC counts through BenchmarkDotNet.
- Measure cache hits and misses separately.
- Measure both normal CORS requests and preflight short-circuiting.
- Measure both allowed and rejected edge-rate-limit paths.
- Measure package-owned request-limit and version-deprecation configuration as cold-start work.
- Measure steady-state Redis limiter calls after connection establishment and Lua-script warmup.
- Make every global setup validate that the intended behavior occurred, so a broken scenario fails
  instead of publishing deceptively fast numbers.
- Document the scenarios, Docker requirement, environment, results, and comparison method in
  `docs/performance.md`.

## Non-goals

- Replacing a Kestrel load test or claiming production throughput/p95 from microbenchmarks.
- Measuring Docker image pull, container startup, Redis connection establishment, or initial Lua
  script loading as request overhead.
- Predicting production Redis latency across networks, TLS, clusters, or cloud providers.
- Exhaustively benchmarking every configuration permutation and error path.
- Benchmarking authentication, authorization, or HTTPS redirection, which are framework phases and
  were not part of the identified coverage gap.
- Changing production behavior to make it easier to benchmark.

## Constraints

- Target .NET 10 and the repository's centrally pinned BenchmarkDotNet, Asp.Versioning,
  StackExchange.Redis, and Testcontainers.Redis versions.
- Preserve the current direct-`RequestDelegate` measurement style where the framework middleware
  can be driven accurately without an HTTP transport.
- Do not combine unrelated features in one baseline group; BenchmarkDotNet ratios are meaningful
  only inside a class with a comparable baseline.
- Use new `DefaultHttpContext` instances per operation. Shared middleware/services and cache stores
  may persist, as they do in an application.
- Keep container and host setup outside measured methods.
- Use a Redis 7.4 image pinned by digest, matching the integration tests.
- Do not silently fall back to an in-memory Redis substitute.

## Approaches Considered

### 1. One hybrid BenchmarkDotNet suite — selected

Keep one executable and add focused benchmark classes. Directly invoke built pipelines for
request-path middleware, use cold-start operations for configuration-only features, and use a
Docker-managed Redis container for distributed rate limiting. This preserves small, interpretable
deltas while one command still proves complete coverage.

Tradeoff: the perf project gains references to the versioning and Redis packages plus
Testcontainers.Redis, and a complete run requires Docker.

### 2. Separate core and Redis benchmark projects

This isolates dependencies and lets core benchmarks run without Docker, but it creates multiple
commands and makes an accidentally incomplete run easy. It conflicts with the decision that Docker
is required for complete coverage.

### 3. Run every feature through a hosted HTTP stack

This resembles an application most closely, but TestServer/Kestrel and HTTP client allocations
bury the middleware deltas the suite is intended to isolate. It remains appropriate for a later
macro load test, not as the primary microbenchmark design.

## Project Structure

The existing benchmark classes remain unchanged except where a shared helper removes duplication.
New responsibilities are split as follows:

- `BenchmarkPipeline.cs`: creates service providers, starts hosted startup checks, builds
  `RequestDelegate` pipelines, and creates standardized contexts/body sinks.
- `ForwardedHeadersBenchmarks.cs`: no-header pass-through and trusted forwarded-header processing.
- `CorsBenchmarks.cs`: allowed-origin GET and OPTIONS preflight.
- `RequestTimeoutBenchmarks.cs`: enabled timeout middleware on a successful non-timed-out request.
- `ResponseCompressionBenchmarks.cs`: a fixed compressible response with and without gzip.
- `CachingBenchmarks.cs`: response-cache and output-cache miss/hit groups with distinct baselines.
- `EdgeRateLimitingBenchmarks.cs`: allowed and already-exhausted/rejected paths.
- `StartupConfigurationBenchmarks.cs`: request-limit and version-deprecation registration, service
  provider construction, option validation, and option resolution.
- `RedisBenchmarkEnvironment.cs`: starts one pinned Redis container in the parent benchmark
  process, publishes its connection string to BenchmarkDotNet child processes, and disposes it
  after the invocation.
- `RedisRateLimitingBenchmarks.cs`: Docker lifecycle and real Redis fixed/sliding/token/concurrency
  limiter operations.
- `PerfImages.cs`: the reviewed Redis image digest used by the performance suite.

`ApiPipeline.NET.Perf.csproj` will reference the versioning and Redis projects and
Testcontainers.Redis. It remains a single executable discovered by `BenchmarkSwitcher`.

## Common Measurement Rules

Every request-path class owns an explicit `[Benchmark(Baseline = true)]` method. The baseline must:

- create the same kind of `DefaultHttpContext`;
- set the same method, path, scheme, headers, endpoint metadata, request services, and body stream;
- invoke the same terminal delegate and write the same payload;
- differ only by omission of the feature under test.

Setup runs one unmeasured request through each pipeline and asserts a semantic postcondition. The
benchmark throws during setup if, for example, a forwarded address did not change, a CORS header
was absent, compression did not set `Content-Encoding`, a cache hit called the terminal, or a Redis
request did not touch the expected limiter.

Cache-miss methods need a unique cache key per invocation. Their baselines perform the same key
generation so the reported ratio does not attribute harness work to caching. Miss and other
state-mutating cases use a fixed invocation count and iteration setup, which bounds retained cache
entries and ensures every measured invocation has the intended starting state. Cache-hit entries
are pre-populated during setup, use stable keys with an expiration longer than the entire run, and
are checked again after measurement. Each response gets a fresh writable body stream.

Short-circuit behavior has its own baseline group. An OPTIONS preflight baseline uses the same
method and request headers, and a rejection baseline uses the same request construction while
omitting the rejecting middleware. These ratios are documented as whole-request comparisons, not
as a pure additive middleware cost, because the feature deliberately prevents the terminal from
running.

## Feature Scenarios

### Forwarded headers

- Baseline: trusted loopback peer with no forwarded-headers middleware.
- Pass-through: middleware enabled, no forwarded headers.
- Active: `X-Forwarded-For` and `X-Forwarded-Proto` from a trusted peer.
- Validation: remote address and scheme equal the forwarded values on the active path.

### CORS

- Baseline: routed GET with the same `Origin` header and terminal.
- Allowed GET: production explicit-origin policy adds `Access-Control-Allow-Origin`.
- Preflight: OPTIONS with `Origin` and `Access-Control-Request-Method` short-circuits with allow
  headers and does not call the terminal.

### Request timeouts

- Baseline: routed request calling a synchronously completing terminal.
- Enabled pass-through: the configured default timeout policy wraps the same successful request.
- Validation: terminal runs, the active terminal observes the framework request-timeout feature
  and a cancellable timeout token, the baseline does not expose that feature, and the request does
  not become a timeout response.
- A deliberately expired request is excluded from the primary timing because timer scheduling and
  wall-clock delay would dominate middleware overhead.

### Response compression

- Baseline: request with `Accept-Encoding: gzip`, terminal writes a fixed compressible payload to a
  sink, no compression middleware.
- Active: identical request/payload with response compression enabled for HTTPS.
- Validation: `Content-Encoding: gzip` is present and the emitted byte count is smaller.
- Use a payload large enough to cross provider thresholds while remaining a microbenchmark; the
  exact size is fixed and documented in the benchmark name.

### Response caching

- Baseline hit/miss harnesses write the same cacheable response without cache middleware.
- Miss: iteration setup assigns a unique URL and one measured invocation performs lookup plus
  storage; the fixed invocation count prevents an unbounded cache during a benchmark run.
- Hit: pre-populated stable URL; terminal invocation count must remain unchanged.
- Response carries explicit public cache headers so framework policy cannot silently bypass it.

### Output caching

- Baseline hit/miss harnesses mirror output-cache request metadata and terminal behavior.
- Miss: iteration setup assigns a unique URL with output-cache endpoint metadata and one measured
  invocation performs lookup plus storage; the fixed invocation count bounds retained entries.
- Hit: pre-populated stable URL; terminal invocation count must remain unchanged.
- Endpoint metadata/policy is created exactly as an application endpoint using output caching.

### Edge rate limiting

- Baseline: same remote IP and terminal without the edge limiter.
- Allowed: a fixed invocation count remains below the per-partition permit limit for the entire
  iteration; iteration setup moves to a fresh partition before the next iteration.
- Rejected: setup exhausts a low-limit partition; the measured request returns 429 without calling
  the terminal.
- Allowed and rejected paths use separate baseline groups because rejection short-circuits.
- Global cleanup verifies the allowed terminal count and rejected status remained stable throughout
  measurement.

### Request-limit startup configuration

- Baseline: create/dispose an equivalent service provider and resolve framework server options
  without package request-limit registration.
- Active: register configured request limits, build the provider, trigger validation, resolve the
  configured Kestrel options, and dispose the provider.
- Validation: resolved maximum request-body size and related configured values equal the inputs.

### Version-deprecation startup configuration

- Baseline: register Asp.Versioning and resolve its options without package deprecation policies.
- Active: register several deprecation/sunset policies through the package, build the provider,
  trigger validation, and resolve `ApiVersioningOptions`.
- Validation: the resolved policy collection contains the configured versions, dates, and links.
- This measures the package-owned work. Runtime version selection/reporting remains Asp.Versioning
  behavior and is not presented as custom middleware overhead.

### Redis-backed rate limiting

- The parent benchmark program starts one pinned Redis 7.4 container before BenchmarkDotNet runs;
  failure to start Docker fails the command. Its connection string is inherited by generated child
  processes through a dedicated environment variable.
- Redis benchmark setup establishes one multiplexer, builds real package pipelines, runs startup
  checks, issues warmup requests, and ensures scripts use the steady-state cached path before
  measurement.
- Benchmark allowed requests for fixed-window, sliding-window, token-bucket, and concurrency
  policies. Fixed invocation counts and limits/windows larger than the measured request count keep
  every operation on the allowed path; post-measurement validation confirms that invariant.
- Benchmark two fixed-window rejection paths separately:
  - **server rejection**: iteration setup creates and exhausts a fresh partition, then exactly one
    measured invocation receives the Redis answer;
  - **cached rejection**: setup receives a Redis denial once, then measured requests exercise the
    package's local deny cache and make zero Redis calls until `Retry-After` expires.
- Include a two-policy request (global plus default) to demonstrate the cost of two sequential
  Redis calls.
- Use unique prefixes per benchmark instance and a fixed non-secret test partition key secret.
- Validation checks status codes and Redis command statistics/counters outside timed methods,
  including one call for server rejection and zero calls for cached rejection.

## Docker and Failure Behavior

The full benchmark command requires a working Docker daemon and permission to start containers.
There is no skip attribute and no catch that converts Docker unavailability into success. The error
message should identify Docker as the missing prerequisite. The parent process starts one container
per benchmark invocation, keeps it alive while all generated child processes run, and disposes it
when BenchmarkDotNet returns.

Image pull, container start, connection, and script warmup appear in total suite wall time but not
in per-operation results. Documentation must distinguish those two meanings of “cost.”

## Verification

Implementation is accepted when all of the following hold:

1. Release build of the perf project succeeds.
2. `--list flat` contains every new benchmark scenario.
3. A Docker-backed `--job Dry` run executes every new scenario once without `NA`, setup failure, or
   semantic-validation exception.
4. A full `--job short` run produces reports for every old and new benchmark class.
5. Existing unit and integration tests for core, versioning, and Redis packages pass.
6. `docs/performance.md` lists every class, distinguishes request and startup measurements, states
   the Docker requirement, records the run environment, and includes the new results or explains
   why machine-specific Redis latency is kept as an artifact rather than a universal baseline.

## Documentation and Comparison Policy

Managed allocation changes are compared directly with the preceding stored baseline. Timing is
compared only on the same machine/runtime and with the same BenchmarkDotNet job. Redis timing is
reported as observed local-container round-trip cost, not an industry-wide threshold.

The repository's existing macro target (full-pipeline p95 no more than 20% above its framework
baseline) remains a separate load-test criterion; these microbenchmarks do not claim to satisfy it.
