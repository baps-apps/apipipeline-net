# ApiPipeline.NET — Architecture & Production Readiness Review

> **Reviewed:** 2026-09-02 · **Resolved:** 2026-09-02 · **Status: ✅ ALL FINDINGS CLOSED**
> **Reviewer role:** Principal .NET Architect / ASP.NET Core framework · AppSec · Performance · SRE · NuGet maintainer
> **Scope:** full repository — `src/ApiPipeline.NET`, `src/ApiPipeline.NET.OpenTelemetry`, `src/ApiPipeline.NET.Versioning`, `samples/`, `tests/`, `perf/`, `docs/`, build & CI
> **Reviewed at:** working tree on `d6c81a2` + staged v2.0.0 version-deprecation delegation
> **Runtime:** net10.0 · verified against `Microsoft.AspNetCore.App 10.0.0`
> **Guiding principle applied:** do not reinvent the wheel

> **How to read this document.** It records one review and its resolution. The **Resolution**
> section below is the current status of every finding — that is the authoritative part. Everything
> from [§1](#1-final-decision) onwards is the review **as written**, in the present tense of the day
> it was written, kept because it is the reasoning that produced the fixes. Where a finding's
> heading carries a ✅/status marker, that marker is the current state and the prose beneath it
> describes the problem as it was. Section 1's verdict and section 2's scorecard carry both the
> original and the current figures.

---

## Resolution

Every finding is closed: 3 P1, 6 P2, 3 P3, all 5 deletions, all 8 maintainability items, all 6
performance items, and both documentation contradictions. Two defects the review did not find were
fixed alongside them.

### Verified, not asserted

Each finding was re-checked by reading the code, not by trusting the CHANGELOG entry that claims it.

| Check | At review | Now |
|---|---|---|
| `dotnet build ApiPipeline.NET.slnx` | Succeeded, **0 warnings** (`TreatWarningsAsErrors=true`) | Succeeded, **0 warnings** |
| `dotnet test ApiPipeline.NET.slnx` | **307 passed, 0 failed** | **341 passed, 0 failed** |
| `dotnet pack ApiPipeline.NET.slnx` | Succeeded — but API compatibility validation **silently never ran** (see §11 below) | Succeeded, all three packages, **48 deliberate breaks enumerated and reviewed** |
| Benchmarks | 6 middleware + 3 partition-key scenarios; no committed baselines | + rate-limiter and 1/8/64 concurrency scenarios; **allocations/op committed** |

Framework behaviour probed during the review, and still true:

| Question | Answer |
|---|---|
| Does the framework already emit rate-limiting metrics? | **Yes** — extracted from `Microsoft.AspNetCore.RateLimiting.dll`: `aspnetcore.rate_limiting.requests` (tags `aspnetcore.rate_limiting.result` = `acquired`/`endpoint_limiter`/`global_limiter`/`request_canceled`, plus `aspnetcore.rate_limiting.policy`), `.active_request_leases`, `.queued_requests`, `.request_lease.duration`, `.request.time_in_queue` |
| Does the framework emit `http.server.request.body.size`? | **No** — only `http.server.request.duration` and `http.server.active_requests` exist in `Microsoft.AspNetCore.Hosting.dll`. The package's claim is correct. |
| Does `System.Net.IPNetwork.TryParse` exist and mask host bits on net10.0? | **Yes** — `IPNetwork.TryParse("10.1.2.3/8")` → `10.0.0.0/8`. The hand-rolled CIDR parser was redundant. |
| Invalid hot-reload behaviour (P1-1) | **Reproduced** — `IConfigurationRoot.Reload()` threw `AggregateException`, and `IOptionsMonitor<SecurityHeadersSettings>.CurrentValue` then threw `OptionsValidationException` on every subsequent read |

### The three release blockers

| Finding | What was done |
|---|---|
| **P1-1** · An invalid configuration reload takes every request to HTTP 500 | New `Configuration/ValidatedSnapshot.cs` (199 lines) + `ValidatedSnapshotWarmup.cs`. **Zero live `IOptionsMonitor` reads remain anywhere in `src/`** — only comments naming it. All five hot-reloaded types (`SecurityHeadersSettings`, `CorrelationIdSettings`, `EdgeRateLimitingOptions`, `CorsSettings`, `RateLimitingOptions`) plus `ResponseCompressionSettings` are served from a last-known-good snapshot. New metric `apipipeline.configuration.reload_rejected`. 7 tests in `InvalidConfigurationReloadTests.cs`, one per reader. |
| **P1-2** · A live configuration change can silently switch rate limiting off | `RateLimiterPolicyResolver.TryResolveEnforceablePolicy` retains the last policy configured under a name across reloads, with `apipipeline.ratelimit.policy_unresolved` per request and an error log once per snapshot. `GetNoLimiter("no-policy")` is now reachable only for a name that was never configured. Failing closed was considered and rejected: it would turn a one-word typo into a total outage, the same class of failure as P1-1. |
| **P1-3** · Two independent CIDR parsers at the trust boundary | One `System.Net.IPNetwork.TryParse` loop in `ForwardedHeadersConfigurator`; the trust-entry count is a by-product of that same parse, so the guard and the trust set it guards cannot diverge. 3 new tests including host-bit masking. |

### Everything else

| Finding | Status | What was done |
|---|---|---|
| **P2-1** CORS provider replacement only warned | ✅ | `Cors/CorsOverrideStartupGuard.cs` replaces `CorsOverrideStartupWarning.cs`: throws in Production when an application default policy coexists with `CorsOptions:Enabled=true`, warns elsewhere. 3 tests. |
| **P2-2** `__EndpointRouteBuilder` could go inert silently | ✅ | `PipelineOrderGuardState.IsKeyReachable` probes a throwaway `ApplicationBuilder` at host start and logs an error naming `RuntimeInformation.FrameworkDescription` if the key is gone; an inconclusive probe is correctly not reported as a broken guard. `CONTRIBUTING.md` gained an 8-item .NET-major framework-behaviour checklist. 2 tests. |
| **P2-3** Edge and main limiters rejected differently | ✅ | New `RateLimiting/RateLimitRejection.cs` is the single 429 writer; both `RateLimiterOptions.OnRejected` and `EdgeRateLimitingMiddleware` call it, so a caller cannot tell which limiter rejected it from the response shape. The edge path's invisible `LogDebug` is deleted — `apipipeline.edge_ratelimit.rejected` is the signal. 2 tests assert parity. |
| **P2-4** `X-RateLimit-*` on every 429 disclosed the quota | ✅ | New `RateLimitingOptions:EmitRateLimitHeadersOnRejection`, default `true` (the 1.x behaviour), mirrored onto the shared `RateLimitPartitioningOptions` so the edge limiter cannot disclose a quota the main limiter was told to withhold. `Retry-After` and `Cache-Control: no-store` stay unconditional. 5 tests. |
| **P2-5** Unsalted API-key hashes are a rainbow-table target | ✅ documented | The only available fix — a hash of a weak key is indistinguishable from a hash of a strong one, so nothing can enforce it. `RateLimitingOptions.KnownApiKeyHashes` and a new "API key entropy is your responsibility" section in `docs/features/rate-limiting.md` state the ≥128-bit RNG requirement, why the hash is deliberately unsalted and fast, and that the list is not secret: it lives in a ConfigMap, in Git, and in every pod that mounts it. |
| **P2-6** `ProcessHost` without `AllowedHosts` was unguarded | ✅ | `ForwardedHeadersConfigurator.GuardHostProcessing` fails Production startup when `AllowedHosts` is unset, empty, `"*"`, or a list containing `*`; warns elsewhere. Matches `HostFilteringOptions`' own `;`/`,` parsing. Deliberately **not** suppressible by `EnforceTrustedProxyConfigurationInProduction` — that flag is for genuinely-trusted topologies, and an open `AllowedHosts` with `ProcessHost` on has no legitimate configuration behind it. 7 tests. |
| **P3-1** Non-namespaced `correlation_id` span tag | ✅ | `apipipeline.correlation_id`, exposed as `ApiPipelineTelemetry.CorrelationIdTagName`. Documented as a breaking trace-query change. |
| **P3-2** `AcceptInboundCorrelationId` guidance | ✅ | `README.md` and `docs/features/correlation-id.md` say to set it `false` on internet-facing APIs. Default unchanged, which the review allowed. |
| **P3-3** No `Cache-Control` default | ✅ documented | New section in `docs/features/security-headers.md`: why there is no default (the correct value is per endpoint, and a wrong global default is worse than none), the RFC 9111 §3.5 shared-cache rule and how a non-conformant intermediary breaks it, per-endpoint and whole-app recipes, and the note that the pipeline's own caches are auth-safe because this is about caches *outside* the process. |
| **§4.1** Duplicated CIDR parser | ✅ | See P1-3. |
| **§4.2** `apipipeline.ratelimit.rejected` duplicates a framework metric | ✅ kept + documented | Not deleted. Kept for `partition_type`, with the overlap stated in `OBSERVABILITY.md` and a warning against summing the two series — the review's own second option. The edge limiter got the separate instrument the review asked for: `apipipeline.edge_ratelimit.rejected`. |
| **§4.3** Versioning decorator surgery | ✅ deleted, not replaced | `ServiceDescriptor` surgery, `DecorationMarker`, `DeprecationReportingTelemetry` and `apipipeline.deprecation.headers_added` are all gone — 278 lines. `AddApiPipelineVersioning` inspects no registered service, and **both** documented ordering constraints are lifted. `Scrutor` was evaluated and declined in `ARCHITECTURE.md`. |
| **§4.4** Dead `DisposeAsync` | ✅ | Deleted, replaced by a comment at `EdgeRateLimitingMiddleware.cs` explaining why there is none. |
| **§4.5 / PERF-1** Per-response `LogDebug` | ✅ | Gone, and `SecurityHeadersMiddleware` no longer takes an `ILogger` at all — one field out of the boxed `OnStarting` tuple. |
| **§7** No rate-limiter or concurrency benchmark; no committed baselines | ✅ | `RateLimiterPipelineBenchmarks` drives a real `UseRateLimiter` pipeline over the package's own registration; `ConcurrentPipelineBenchmarks` runs the full pipeline at 1, 8 and 64 concurrent requests through **one** partition, where the only lock contention lives. `docs/performance.md` commits **allocations per operation** for all four classes, with the reasoning for committing allocations and not timings. Per-request allocation is flat — 3.09 → 2.99 → 2.97 KB across a 64× concurrency increase. |
| **§8.1** 278 lines to record one counter | ✅ | See §4.3. |
| **§8.2** `__EndpointRouteBuilder` maintenance event | ✅ | See P2-2. |
| **§8.3** Two public APIs for everything | ✅ | All eleven individual `UseApiPipeline*` methods are `[Obsolete]`, with a message naming the four invariants they bypass. `ApiPipelineBuilder`'s phase switch suppresses `CS0618` locally — it *is* the ordering, so it is the one correct caller — and the test project suppresses it project-wide, because tests of an obsolete-but-still-shipping method are what keep it correct until removal. |
| **§8.4** Comment-to-code ratio | ✅ | `ForwardedHeaders/ForwardedHeadersConfigurator.cs` extracted: parse, count, trust-set guard and host guard each became a named method with its explanation as type-level documentation. `ApplicationBuilderExtensions.cs` 592 → ~440 lines. |
| **§8.5** Three registration tiers | ✅ | `ApiPipelineServiceRegistrationOptions` deleted with its nine flags; `AddApiPipeline(configuration)` registers everything, and every feature is inert until its own `Enabled` is true. `docs/migration.md` §19 maps each removed flag and calls out the two that do not map cleanly: `AddApiPipelineRequestLimits` also gated the Kestrel hardening, and `AddApiPipelineRequestSizeTracking` has no `Enabled` flag to map to. |
| **§8.6** Smuggled `int.TryParse` | ✅ | New `Options/RateLimitPartitioningOptions.cs`, bound by both limiters from the same section; it holds no collections, so re-binding is idempotent. |
| **§8.7 / PERF-5** Compression/caching doc contradiction | ✅ | There were **three** wrong statements, not two — `response-caching.md:60` carried the same error the review found at `response-compression.md:42,174`. All three now agree with the code, and `response-compression.md` gained a "Compression and caching" section with the real trade-off table, why outside-the-caches is the default (a `Vary` mistake is a broken response; re-compression is only a slow one), and how to measure whether your payload shape justifies the other choice. |
| **§8.8** Stale review in `docs/` | ✅ | `docs/archive/` created with a README stating plainly that nothing in it describes the package as it stands. Both reviews moved there — including this one. |
| **§11** API compatibility validation off | ✅ | See "What the review missed" below: it was off for a second reason the review did not identify. |
| **PERF-2, PERF-3** Allocation costs unmeasured | ✅ measured | Both findings said "measure before changing anything". The numbers are committed, and they say don't: correlation id's ~550–700 B is the `OnStarting` tuple and the `BeginScope` array, which those signatures make unavoidable, and the partition keys match what the framework's own partitioners allocate. |
| **PERF-4** `IMiddleware` factory activation | ✅ | `CorrelationIdMiddleware` and `RequestSizeMiddleware` are convention-based; the per-request `IMiddlewareFactory.Create` + transient resolution + `Release` is gone, along with both DI registrations. The DI-registration test was inverted to assert they are *not* registered, so a re-added registration fails the build. |
| **PERF-6** `MimeTypes` linear scan | ✅ evaluated | Left as `string[]`, with the reasoning at the call site: the list is ten entries, `ResponseCompressionProvider` scans it linearly per response, and a linear scan of ten strings beats hashing the content type first. Reconsider only for a deployment configuring dozens of entries. |
| **§3** Build-vs-buy never written down | ✅ | `docs/ARCHITECTURE.md` now has "HSTS is a deliberate re-implementation of `HstsMiddleware`" plus explicit *declined* sections for `NetEscapades.AspNetCore.SecurityHeaders`, `Microsoft.AspNetCore.HeaderPropagation`, and `Scrutor`. |

### What the review missed

Two defects surfaced while implementing the fixes, neither of which the review found.

1. **API compatibility validation had never run — for a second reason.** The review said the
   baseline was gated behind an unset repository variable, which was true. It was *also* gated
   behind `EnablePackageValidation Condition="'$(IsPackable)' == 'true'"` in
   `Directory.Build.props`, where the SDK has **not yet defaulted `IsPackable`** — so the condition
   was always false and no version of this package has ever been API-checked, variable or not.
   Moved to a new `Directory.Build.targets`, where `IsPackable` is known and a project can override
   its own baseline; pinned to the last released version (`1.1.0`, per the tag) rather than a
   variable that can silently switch it off. Verified live: deleting the suppression file makes
   `pack` fail with 48 enumerated breaks.
2. **A third copy of the compression/caching contradiction.** The review found two wrong lines in
   `response-compression.md`. `response-caching.md:60` said the same wrong thing.

### Where the implementation departed from the review — correctly

**P1-1's suggested fix was wrong in two ways**, and the implementation says so at
`ValidatedSnapshot.cs:56-83` rather than following it:

1. The review's first snippet used `monitor.OnChange(next => _current = next)`. `OptionsMonitor`
   never invokes a change listener for an instance that failed validation, so that fix would have
   kept the last known good value **silently** — and silence was the second half of P1-2's defect.
2. The review's second snippet used `monitor.Get(Options.DefaultName)` inside the guarded reload.
   That is the exact call whose cache entry is already evicted and which therefore re-throws
   forever. The implementation builds from `IOptionsFactory<T>` instead, which reads configuration
   directly and consults no cache — so it is also order-independent with respect to other
   reload-token callbacks.

It also documents a residual the review missed: `ValidateOnStart()` is itself implemented as
`Configure<IOptionsMonitor<TOptions>>(...)`, so registering it constructs a monitor whose own
listener still throws on an invalid reload. That exception is unreachable from request handling, and
`InvalidConfigurationReloadTests` asserts it is the *only* exception a reload raises — which would
also catch this package regressing into throwing from a listener of its own. Keeping fail-fast
startup validation was the right trade, and it is written down rather than papered over.

**Three smaller departures:**

- **P2-3's log line.** The review offered "raise the edge log to `Information` with a sampling guard
  (or leave it as a metric only)". Metric-only was taken and the log deleted rather than raised: a
  sampling guard is one more thing to get wrong, and the counter answers the same question without
  one.
- **P2-4's shape.** The review offered "gate the 429 headers on `EmitRateLimitHeaders` too, or add
  `EmitRateLimitHeadersOnRejection` defaulting to true". The second, because the first conflates two
  decisions with different costs — success-path headers cost a middleware frame on every request,
  rejection headers cost nothing — and would silently change rejection behaviour for anyone who had
  turned the success-path headers off.
- **§8.3's mechanism.** `[Obsolete]`, not `internal`. Making them internal breaks every consumer
  using them with no migration window; the attribute gives one and still closes the door for v3.

### What is left

Nothing in this review. One adjacent item it did not raise: `docs/migration.md` documents 26
breaking changes for 2.0.0, and `CHANGELOG.md` has no `[1.1.0]` section even though `v1.1.0` is
tagged and published — so a consumer on 1.1.0 cannot see what they are upgrading from. Worth
reconciling before 2.0.0 ships.

The one deliberate loose end in the code: the individual `Use*` methods still exist, deprecated, for
one major version. Removing them in v3 is the last step, and it is why API Design scores 8 rather
than 9 below.

---

## 1. FINAL DECISION

> **Verdict as written: ❌ DO NOT APPROVE.** **Verdict now: ✅ APPROVED** — all three blockers below
> are fixed and tested. The original decision is kept verbatim because it is the argument that got
> them fixed.

### ❌ DO NOT APPROVE FOR ORGANIZATION-WIDE PRODUCTION USE *(as written, 2026-09-02)*

**This is a close call, and the architecture is not the problem.** The build-vs-buy discipline here is better than most internal platform packages I have reviewed: it delegates rate limiting to `System.Threading.RateLimiting`, caching to `OutputCache`/`ResponseCaching`, compression to `ResponseCompression`, CORS to `CorsMiddleware`, timeouts to `RequestTimeouts`, limits to Kestrel, versioning to `Asp.Versioning`, error shape to `IProblemDetailsService`, and telemetry to `System.Diagnostics.Metrics` — and it explicitly refuses to publish its own `ActivitySource`. Dependency count is near zero. The reasoning is written down in the code.

The block is a single verified reliability defect with organization-wide blast radius:

> **A typo in a hot-reloaded configuration value turns a healthy, already-Ready pod into 100% HTTP 500.**

Because the package advertises hot-reload as a feature, reads `IOptionsMonitor<T>.CurrentValue` on the request path in four places, and uses `ValidateDataAnnotations`, an invalid reload makes `CurrentValue` throw for the life of the process. In the deployment this package explicitly targets — Kubernetes, `appsettings.json` with `reloadOnChange: true` (the `WebApplication.CreateBuilder` default), ConfigMap mounted as a volume — a one-character ConfigMap edit is a sev-1 across every consuming service. `ValidateOnStart` provides no protection: it already ran.

That is not an acceptable property to ship to 30 applications, and it is not a design flaw — **the package already invented the correct pattern in three places** (`RateLimiterPolicyResolver`, `CompressionExclusions`, `LiveConfigCorsPolicyProvider._configuredPolicy` all cache a guarded snapshot). It just is not applied uniformly.

**Estimated time to approval: 1–2 days.** Fix P1-1, P1-2, P1-3 below, add the three missing tests, and this becomes an approve. Nothing in section 3 or 4 blocks release.

> **Outcome:** all three were fixed, with a test per affected reader, and the fix for P1-1 is better
> than the one proposed here — see [Resolution](#resolution). The estimate held.

---

## 2. PRODUCTION SCORECARD

Both columns, so the delta is visible: **At review** is the score as assessed on 2026-09-02;
**Now** is the score after every finding was closed. The basis column explains the original score
and what moved it.

| Area | At review | Now | Basis, and what changed |
|---|---:|---:|---|
| Security | 8 | **10** | *Then:* genuinely secure-by-default; fails fast on the dangerous forwarded-headers cases; API-key partitioning correctly requires an allow-list. Lost points for globally replacing `ICorsPolicyProvider` and for a divergent edge-limiter rejection path. *Now:* the CORS conflict and `ProcessHost` both fail Production startup, the two rejection paths are one writer, and the 429 quota headers are switchable. |
| Performance | 8 | **9** | *Then:* `FrozenDictionary`/`FrozenSet` snapshots, precomputed policy signatures, span-based validation, cached HSTS string, real BenchmarkDotNet project. Lost points for per-request state-tuple allocations, `IMiddleware` factory activation, and a `LogDebug` on every response. *Now:* both middleware are convention-based, the `LogDebug` is gone, and the remaining tuple allocations are measured and shown to be irreducible. Not 10: the framework's own `OnStarting`/`BeginScope` signatures set the floor. |
| Maintainability | 6 | **9** | *Then:* dependence on the framework-internal `__EndpointRouteBuilder` key, duplicated CIDR parsing, `ServiceDescriptor` surgery to decorate `IReportApiVersions`, 258 public members across 3 packages. *Now:* −278 lines of container surgery, one CIDR parser, two lifted ordering constraints, a self-diagnosing order guard, and a .NET-upgrade checklist for every framework-internal assumption. |
| Readability | 7 | **9** | *Then:* comments explain *why*, which is rare and valuable — but several files are more comment than code, itself a signal the behaviour is subtler than it should be. *Now:* the worst offender is extracted into `ForwardedHeadersConfigurator`, where each rule is a named method and its explanation is type-level documentation. |
| Observability | 7 | **9** | *Then:* correctly publishes no `ActivitySource`; correctly uses the semconv name for body size. Lost points for `apipipeline.ratelimit.rejected` duplicating `aspnetcore.rate_limiting.requests`, and a non-semconv `correlation_id` tag. *Now:* the overlap is documented with a don't-sum warning, the edge path has its own instrument, the tag is namespaced, and two "not doing what your config says" counters exist with runbook entries. |
| Reliability | 5 | **9** | *Then:* the hot-reload defect; silent loss of rate limiting when a policy name disappears from a live config; an `IAsyncDisposable` middleware that is never disposed. *Now:* all three closed — last-known-good snapshots for every hot-reloaded type, a retained-policy fallback that is loud rather than silent, and the dead code deleted. |
| Microsoft Standards Alignment | 8 | **9** | *Then:* options pattern, `ValidateOnStart`, `IConfigureOptions`, `IHostedService` guards, `FrameworkReference`, framework middleware throughout. Ordering deviated from the docs table in two places — deliberately and defensibly, but one of the two was undocumented. *Now:* the compression/caching order is documented with its trade-off table, and convention-based middleware matches the framework's own guidance. |
| Dependency Hygiene | 9 | **10** | *Then:* CPM, `NuGetAudit` at `low`/`all`, Source Link + embedded PDB, deterministic builds, actions pinned to SHAs, `packageSourceMapping` against dependency confusion, CI asserts no private-feed deps leak into the nupkg. Best-in-class — but package validation was listed as present when it was in fact never running. *Now:* it runs, against a pinned baseline, with all 48 deliberate breaks enumerated in a reviewable file. |
| Test Quality | 8 | **9** | *Then:* 307 behavioural tests through a real `TestServer`, organised by guarantee. Missing: invalid-hot-reload, concurrency, and load. *Now:* 341 tests, including one per invalid-reload reader and a concurrency benchmark. External load testing is still the consumer's, which is correct — hence 9, not 10. |
| API Design | 6 | **8** | *Then:* 29 `Add*`/`Use*` extension methods **plus** a phase builder means two supported ways to do everything, and both must stay working forever. *Now:* the nine registration flags and their options type are deleted, and the eleven individual `Use*` methods are `[Obsolete]`. **The only score still below 9, deliberately:** they still exist for one major version. Removing them in v3 is the last step. |
| Kubernetes Readiness | 7 | **10** | *Then:* the forwarded-headers trust guard is the single best thing in this repo. Probe exclusions are right. Process-local limiter semantics are stated honestly. Lost points for the ConfigMap-reload outage vector. *Now:* that vector is closed, and a rejected reload is a metric plus an error log rather than an outage. |
| Documentation | 9 | **10** | *Then:* genuinely excellent, with per-feature docs, runbook, troubleshooting, migration. One factual contradiction found (§8) — three copies of it, as it turned out. *Now:* all three fixed, build-vs-buy written down, the hot-reload contract documented, and the reviews moved out of `docs/` where they read as current. |

**Overall: 73 / 100 at review → ~93 / 100 now.**

---

## 3. REINVENTED-WHEEL REPORT

| Current custom implementation | Existing Microsoft / industry solution | Why the custom code exists | Verdict | Priority |
|---|---|---|---|---|
| `ApplicationBuilderExtensions.CountConfiguredTrustEntries` + inline `Split('/')` CIDR parser (~55 lines, **duplicated twice**) | `System.Net.IPNetwork.TryParse` (net8+, **verified working on net10**) | Predates `IPNetwork.TryParse`, or was never revisited | **DELETE** — replace both copies with one `IPNetwork.TryParse` loop | P1 |
| `CorrelationIdPropagationHandler` | `Microsoft.AspNetCore.HeaderPropagation` (official Microsoft package: `AddHeaderPropagation` + `AddHeaderPropagation()` on the client builder) | Avoids a package reference; ~20 lines | **SIMPLIFY or document the choice** — the official package covers this exactly, including multi-header and value transforms | P3 |
| `SecurityHeadersMiddleware` (202 lines) | `UseHsts()` for HSTS; `NetEscapades.AspNetCore.SecurityHeaders` for the rest (mature, widely adopted, actively maintained) | Needs to run before every short-circuiting phase, and `UseHsts` cannot emit the non-HSTS headers | **KEEP, but narrow** — the loopback/excluded-host and `RequireHttps` logic is a faithful re-implementation of `HstsMiddleware`. Either delegate HSTS to `UseHsts()` inside this phase, or state in the docs that this is a deliberate re-implementation and what it adds. | P2 |
| `apipipeline.ratelimit.rejected` counter | `aspnetcore.rate_limiting.requests` with `result` + `policy` tags (**verified present in the framework**) | Predates the framework metric; adds a `partition_type` dimension | **SIMPLIFY** — the only non-duplicative dimension is `partition_type`. Either drop the counter and document how to query the framework metric, or keep it and say in `OBSERVABILITY.md` that it overlaps. | P2 |
| `EdgeRateLimitingMiddleware` (137 lines) | `UseRateLimiter` cannot be instanced twice with different policy sets; `PartitionedRateLimiter` is the framework primitive | Real framework gap: a second `RateLimiterMiddleware` would fail every endpoint carrying `[EnableRateLimiting("name")]` | **KEEP** — justified, and it does use the framework primitive rather than hand-rolling a limiter | — |
| `RateLimitPolicySignature` + policy-name-in-partition-key | none | Real framework behaviour: `RateLimitPartition.Get*Limiter`'s options factory runs **once per partition key** and caches forever, so a config reload has no effect on existing partitions | **KEEP** — this is exactly the kind of thing a standardization layer should own | — |
| `DenyAllRateLimiter` | none — `FixedWindowRateLimiter` throws on `PermitLimit = 0` | Turning the intended 429 into an unhandled 500 | **KEEP** — 71 lines, correct, and the comment explains the framework constraint | — |
| `RateLimitPartitionKeys.NormalizeIp` (IPv4-mapped collapse + IPv6 prefix masking) | no BCL equivalent for masking an address to a prefix | RFC 6177: a `/128` partition hands one host 2^64 free buckets | **KEEP** — security-relevant and unavailable elsewhere | — |
| `LiveConfigCorsPolicyProvider` (224 lines, `services.Replace(ICorsPolicyProvider)`) | `AddCors` + named policies; no framework support for hot-reloading origins | Hot-reload of allowed origins without a restart | **KEEP with reservations** — see §5 and §6/P2-1. Replacing a host-wide framework service is the most aggressive move in the repo. |  P2 |
| `ApiPipelineBuilder` phase enum | plain `app.Use*()` calls in `Program.cs` | Makes the four ordering invariants unbreakable by call order | **KEEP** — this is the package's core value proposition | — |
| `PipelineOrderGuard` reading `"__EndpointRouteBuilder"` | no public API distinguishes an explicit `UseRouting()` from `WebApplication`'s auto-inserted one | Catches a genuinely silent failure | **KEEP, but harden** — see §6/P2-2 | P2 |
| `VersioningServiceCollectionExtensions` descriptor surgery + `DecorationMarker` | `Scrutor.Decorate<T>()` (mature, widely adopted) | Avoids a dependency; ~40 lines of `ServiceDescriptor` manipulation | **SIMPLIFY** — this is the single most fragile block of code in the repo, and it exists to add one metric | P2 |
| `services.AddOptions<RateLimitingOptions>().Configure(...)` with a manual `int.TryParse` of one raw config key (`ServiceCollectionExtensions.cs:129-141`) | binding the section once | Working around the config binder **appending** to `List<RateLimitPolicy>` on a second `Bind` | **REFACTOR** — the workaround is correct but the shape is wrong. Extract `IPv6PartitionPrefixLength` into a shared options type both limiters bind, or have edge rate limiting depend on `AddApiPipelineRateLimiting`. | P2 |
| `RateLimiterRegistration`: a **second, unvalidated** `configuration.GetSection(...).Get<RateLimitingOptions>()` at registration time, purely to enumerate policy names | one bind | Named policies must be registered at startup | **ACCEPT with a comment** — unavoidable given `AddPolicy`'s signature, but currently undocumented at the call site | P3 |
| Exception handling / `ProblemDetails` | `AddProblemDetails()` + `UseExceptionHandler()` | **Not implemented — deliberately delegated** | **CORRECT.** Documented in the README, the runbook, three feature docs, the sample, and kept compiling by `ConsumerErrorHandling.cs`. This is the right answer and it was reasoned about. | — |
| Structured logging / tracing abstractions | `ILogger`, `Activity` | **Not implemented** | **CORRECT** | — |

---

## 4. WHAT SHOULD BE DELETED

### ✅ DONE · 4.1 The duplicated CIDR parser — delete ~55 lines

**Current** (`ApplicationBuilderExtensions.cs:445-470` and again at `ApplicationBuilderExtensions.cs:562-596`): `network.Split('/')`, `IPAddress.TryParse`, address-family-dependent max-prefix check, `int.TryParse`, then `new IPNetwork(...)`. The same rules are written twice — once to build the options and once to count "configured" entries — so a fix to one is a silent divergence from the other.

**Replacement:**
```csharp
private static bool TryParseKnownNetwork(string value, out IPNetwork network) =>
    IPNetwork.TryParse(value, out network);
```
Verified on net10.0: `IPNetwork.TryParse` validates the prefix length against the address family and masks host bits (`10.1.2.3/8` → `10.0.0.0/8`), which is strictly more correct than the current code's behaviour of passing an unmasked address to the constructor.

**Why better:** one parser instead of two, framework-maintained, and the trust-set count can no longer disagree with the trust set itself — which matters, because that disagreement is what decides whether production startup fails.

**Migration complexity:** trivial. `ForwardedHeadersTests` and `ForwardedHeadersTrustBoundaryTests` already cover the behaviour.

### ✅ DONE (documented, not deleted) · 4.2 `apipipeline.ratelimit.rejected` — delete or narrow

**Current:** a custom counter with `policy_name` and `partition_type`.

**Replacement:** `aspnetcore.rate_limiting.requests{aspnetcore.rate_limiting.result="global_limiter"}` — verified present in `Microsoft.AspNetCore.RateLimiting.dll` on this runtime, already carrying a policy tag, and already collected by anyone using `AddAspNetCoreInstrumentation()`.

**Why better:** an operator's dashboards are built on `aspnetcore.*`. A parallel series with different tag spellings means two numbers that must be reconciled during an incident. Keep the counter only if `partition_type` is genuinely operationally valuable — and if you keep it, say so explicitly in `OBSERVABILITY.md`.

**Caveat:** the edge limiter does **not** run through `RateLimiterMiddleware`, so its rejections produce no framework metric. That is the one case where a custom counter is load-bearing — give it its own instrument rather than sharing one with the framework-covered path.

**Migration complexity:** breaking for any dashboard already using it. Do it in v2.0.0, not later.

### ✅ DONE (deleted, not replaced) · 4.3 `VersioningServiceCollectionExtensions` decorator surgery — replace

**Current:** `services.LastOrDefault(d => d.ServiceType == typeof(IReportApiVersions) && !d.IsKeyedService)`, `services.Remove`, `ServiceDescriptor.Describe`, a hand-written three-way `CreateInstance` for instance/factory/type descriptors, and a private `DecorationMarker` class for idempotency.

**Replacement:** `Scrutor`'s `services.Decorate<IReportApiVersions, DeprecationReportingTelemetry>()`, or — better — delete the decorator entirely and drop `apipipeline.deprecation.headers_added`.

**Why better:** ~40 lines of container internals, a documented ordering constraint (`AddApiPipelineVersioning` must come after every `IReportApiVersions` registration), and an entire `IHostedService` (`VersionReportingStartupWarning`, 106 lines) exist **solely to count one header**. That is a poor value-to-fragility ratio. If a consumer needs the count, the same information is derivable from `http.server.request.duration` joined against the versions they know are deprecated.

**Migration complexity:** removing the metric is a breaking observability change; taking the `Scrutor` dependency is a one-line change but adds a dependency to serve one metric — which is itself an argument for deletion.

### ✅ DONE · 4.4 `EdgeRateLimitingMiddleware.DisposeAsync` — delete (it is dead code)

`app.UseMiddleware<T>()` activates convention-based middleware via `ActivatorUtilities.CreateInstance` and **never disposes it**. The `IAsyncDisposable` implementation cannot run. It reads as lifecycle management and is not. Either delete it (documenting that the limiter lives for the process, which is correct and harmless) or register the limiter as a disposable singleton in DI.

### ✅ DONE · 4.5 `logger.LogDebug("Security headers applied to response")` — delete

`SecurityHeadersMiddleware.cs:160`. It fires on **every response**, carries no request-identifying information, and the `ILogger` is threaded through the `OnStarting` state tuple purely to reach it. Deleting it removes one tuple field, one log-level check per response, and a log line that no operator has ever found useful.

---

## 5. WHAT SHOULD REMAIN CUSTOM

1. **`ApiPipelineBuilder`'s phase ordering.** This is the reason the package should exist. Four invariants — auth before the caches, security headers before every short-circuit, edge limiting before auth, identity limiting after auth — are encoded in an `enum`'s declaration order so no consumer can get them wrong by reordering `With*` calls. Thirty applications each hand-ordering fifteen middleware is thirty chances to cache an authenticated response for an anonymous caller. Keep it.

2. **The forwarded-headers trust guard.** `ForwardedHeadersMiddleware` computes `checkKnownIps = KnownIPNetworks.Count > 0 || KnownProxies.Count > 0` and, when false, applies `X-Forwarded-*` from **any** peer with no trust check. This package fails Production startup on that state, and separately warns on the loopback-only state that silently collapses every client into one rate-limit bucket. It additionally gates the middleware on `RemoteIpAddress is not null`, because a null peer address (Kestrel on a Unix domain socket — a service-mesh sidecar) bypasses a correctly configured trust set entirely. That is a real, reachable, subtle framework footgun and this is the single most valuable 80 lines in the repo.

3. **`RateLimitPolicySignature` + partition-key scoping.** Without it, a config reload changes nothing for any partition that already exists. This is a genuine framework behaviour that no amount of `IOptionsMonitor` wiring fixes.

4. **`NormalizeIp`.** IPv4-mapped-IPv6 collapse and `/64` masking are security-relevant and have no BCL equivalent.

5. **`DenyAllRateLimiter`.** The framework makes the obvious approach throw a 500 instead of returning the intended 429.

6. **`GlobalLimiter` deference to endpoint policies** (`RateLimiterRegistration.cs:88-91`). ASP.NET Core evaluates the global limiter **in addition to** the endpoint policy, so the effective limit is `min(default, named)` and `RequireRateLimiting("generous")` can never raise a limit. Returning `GetNoLimiter` when the endpoint declares its own policy is a correct and non-obvious fix.

7. **`RateLimiterPolicyResolver`'s frozen snapshot.** Correct pattern, correct data structures, zero per-request DI resolution. This is the pattern the rest of the package should adopt (see P1-1).

8. **String-based `ForwardedHeadersSettings` / `RequestLimitsOptions`.** `ForwardedHeadersOptions.KnownProxies` is `IList<IPAddress>` and `Limits.MinRequestBodyDataRate` is a `MinDataRate` — neither is bindable from `IConfiguration`. A string-shaped, validated options type mapped through `IConfigureOptions<T>` is the correct pattern and is exactly what a standardization layer is for.

9. **Delegating exception handling to the application.** Not writing a body in `OnRejected` so a consumer's `UseStatusCodePages` produces one RFC 9457 shape for every error is the right call, and it is documented in five places.

---

## 6. SECURITY FINDINGS

### P0 — CRITICAL RELEASE BLOCKER

None.

### P1 — MUST FIX

#### ✅ DONE · P1-1 · An invalid configuration reload takes every request to HTTP 500 (availability)

**Reproduced.** Reading `IOptionsMonitor<T>.CurrentValue` on the request path, combined with `ValidateDataAnnotations()`, means a validation failure introduced by a *live reload* throws on every subsequent read:

```
before: XFrameOptionsValue=DENY
Reload() THREW: AggregateException
CurrentValue THREW: OptionsValidationException :: DataAnnotation validation failed for
  'SecurityHeadersSettings' members: 'XFrameOptionsValue' with the error:
  'XFrameOptionsValue must be 'DENY' or 'SAMEORIGIN'.'
```

Affected request-path readers:

| Location | Reads |
|---|---|
| `SecurityHeadersMiddleware.Invoke:51` | `_settings.CurrentValue` |
| `CorrelationIdMiddleware.InvokeAsync:61` | `_settings.CurrentValue` |
| `EdgeRateLimitingMiddleware.Invoke:91` and inside the partition factory `:53` | `_options.CurrentValue`, `_rateLimitingOptions.CurrentValue` |
| `LiveConfigCorsPolicyProvider.GetPolicyAsync:87` | `_settingsMonitor.CurrentValue` |

**Failure scenario:** an operator edits a Kubernetes ConfigMap mounted as a volume — `XFrameOptionsValue: "ALLOW-FROM https://..."`, `MaxLength: 4`, `IPv6PartitionPrefixLength: 0`, a malformed `Policies[n]`. `reloadOnChange: true` is the `WebApplication.CreateBuilder` default. Result: (a) the exception escapes into the file-watcher's change-token callback, so any `OnChange` registration ordered after the throwing one never runs; (b) every request through the pipeline returns 500. Liveness probes that bypass the pipeline keep passing, so Kubernetes does not restart the pod — the outage persists until someone reverts the ConfigMap. If probes *do* go through the pipeline, the pod restarts and then fails `ValidateOnStart`, i.e. CrashLoopBackOff. `ValidateOnStart` gives false assurance: it already ran.

**Fix — the package already has the pattern.** `RateLimiterPolicyResolver`, `CompressionExclusions`, and `LiveConfigCorsPolicyProvider._configuredPolicy` all cache a snapshot and refresh it in a guarded `OnChange`. Apply it uniformly:

```csharp
// One shared helper; last-known-good semantics on every hot-reloaded options type.
internal sealed class ValidatedSnapshot<T> where T : class
{
    private readonly IDisposable? _reload;
    private volatile T _current;

    public ValidatedSnapshot(IOptionsMonitor<T> monitor, ILogger logger)
    {
        _current = monitor.CurrentValue;                 // startup: ValidateOnStart already guards this
        _reload = monitor.OnChange(next => _current = next);
        // OptionsMonitor invokes OnChange only for instances that validated successfully,
        // so a failed reload leaves _current at the last known good value.
    }

    public T Current => _current;
}
```

…and wrap the reload itself so the throw never escapes into the watcher thread:

```csharp
_reload = ChangeToken.OnChange(
    () => configuration.GetReloadToken(),
    () =>
    {
        try   { _current = monitor.Get(Options.DefaultName); }
        catch (OptionsValidationException ex)
        {
            logger.LogError(ex,
                "Configuration reload produced invalid {Options} and was ignored; the previous " +
                "values remain in effect.", typeof(T).Name);
        }
    });
```

**Also required:** a test. `HotReloadAndScopeTests` covers *valid* reloads only (`:88`, `:135`). Add `Reload_WithInvalidValue_KeepsPreviousSettings_AndServesTraffic` for each of the four readers. This is the highest-value test missing from an otherwise strong suite.

#### ✅ DONE · P1-2 · A live configuration change can silently switch rate limiting off (security)

Named policies are registered once, at startup, from a separate unvalidated bind (`RateLimiterRegistration.cs:104`). The per-request partition factory resolves the policy from the *live* snapshot (`resolver.ResolvePolicy(policyName)`), and when it finds nothing:

```csharp
return policy is not null
    ? GetPartitionOrFallback(...)
    : RateLimitPartition.GetNoLimiter("no-policy");   // ← rate limiting is now OFF
```

**Failure scenario:** an operator renames `strict` to `standard` in a live ConfigMap, or removes a policy that endpoints still reference via `RequireRateLimiting`. `DefaultPolicy` no longer matches any policy name, `ResolvePolicy` returns null, and every request gets `GetNoLimiter`. Rate limiting is silently and completely disabled on a pod that is passing every health check. Nothing logs. Nothing meters. (Note: if the reload *also* fails `IValidatableObject` validation, P1-1 fires first and you get 500s instead — so the two defects mask each other depending on which invalid edit is made.)

**Fix:** `GetNoLimiter` is the wrong fail state for a security control. Log an error once per snapshot change and fall back to the last known good policy, or fail closed with `DenyAllRateLimiter`. Whichever you choose, add a metric — `apipipeline.ratelimit.policy_unresolved` — and say in `docs/features/rate-limiting.md` what happens when a policy name disappears at runtime. Right now the docs say only "adding new policies requires an app restart", which does not cover removal or rename.

#### ✅ DONE · P1-3 · Delete the duplicated CIDR parser (maintainability, security-adjacent)

Two independent implementations of the same parse decide (a) what goes into `KnownIPNetworks` and (b) whether the production trust-set guard fires. A divergence between them means the guard's verdict stops matching the trust set it is guarding. Replace both with `IPNetwork.TryParse` — see §4.1. Filed as P1 not because it is exploitable today, but because it is the trust boundary, it is duplicated, and the fix is fifteen minutes.

### P2 — SHOULD FIX

#### ✅ DONE · P2-1 · Replacing the host-wide `ICorsPolicyProvider` is the most aggressive act in the package

`ServiceCollectionExtensions.cs:459`:
```csharp
services.Replace(ServiceDescriptor.Singleton<ICorsPolicyProvider, LiveConfigCorsPolicyProvider>());
```
This takes over **every** default CORS lookup in the process. A consumer's own `AddCors(o => o.AddDefaultPolicy(...))` stops being consulted. The mitigations are real and thoughtful — named lookups delegate to `DefaultCorsPolicyProvider`, and `CorsOverrideStartupWarning` logs a warning naming the exact remedy — but a **warning is not a guardrail**. Warnings scroll past in a pod log during a rollout. In an org-wide package, "your CORS policy silently stopped applying, and we logged about it" is a browser-facing production outage waiting for the one team that misses the line.

**Recommendation:** promote to a startup **failure** in Production when both an application default policy and `CorsOptions:Enabled=true` are present, with the same message. Two configurations are both claiming the default policy; that is an ambiguity, not a preference. Keep the warning in non-Production.

#### ✅ DONE · P2-2 · `PipelineOrderGuard` depends on a framework-internal property key

```csharp
private const string ExplicitUseRoutingKey = "__EndpointRouteBuilder";
```
This is `EndpointRoutingApplicationBuilderExtensions`' private marker. It is the only signal distinguishing an explicit `UseRouting()` from `WebApplication`'s auto-inserted one, the choice is documented, and `PipelineOrderGuardTests.Guard_DetectsExplicitUseRouting_ViaFrameworkPropertyKey` fails if it is renamed. That is the right mitigation. But:

- A renamed key makes the guard **silently decorative** in production. The test failing in CI only helps if someone runs CI on the .NET N+1 preview *before* consumers upgrade.
- ASP.NET Core changes this kind of internal without a release note.

**Recommendation:** on `IHostedService` start, assert the key is *reachable* — build a throwaway `ApplicationBuilder`, call `UseRouting()`, and confirm the key appears. If it does not, log an error saying the guard is now inert and which .NET version broke it. A guard that knows when it has stopped working is worth twice one that does not. Add the .NET-upgrade checklist item to `CONTRIBUTING.md`.

#### ✅ DONE · P2-3 · Edge rate limiting and the main limiter reject differently

`EdgeRateLimitingMiddleware.Invoke:105` writes its own 429 (`CacheControl: no-store`, `Retry-After`) and never calls `RateLimiterOptions.OnRejected`. So an edge rejection:

- emits **no** `X-RateLimit-Limit` / `X-RateLimit-Window`, while a main-limiter rejection always does;
- produces no framework `aspnetcore.rate_limiting.requests` measurement;
- is a `LogDebug` (invisible at default log levels) where the main path is a metric.

**Impact:** during an actual flood — precisely when the edge limiter is doing its job — an operator sees an unexplained 429 rate with no matching rate-limiting metric movement, because the framework counter never fires and the custom counter's `policy_name="edge"` is easy to miss.

**Recommendation:** unify the rejection shape. Factor the header-writing into a shared helper used by both `OnRejected` and the edge middleware, raise the edge log to `Information` with a sampling guard (or leave it as a metric only), and give the edge limiter its own instrument as noted in §4.2.

#### ✅ DONE · P2-4 · `X-RateLimit-*` on every 429 discloses quota configuration to anonymous callers

`RateLimiterRegistration.cs:66-77` emits `X-RateLimit-Limit` and `X-RateLimit-Window` on every rejection regardless of `EmitRateLimitHeaders`. The reasoning ("it's free, and a throttled caller is the one that needs it") is sound for a trusted client, but it hands an unauthenticated attacker the exact permit limit and window to calibrate against. Low severity — the limit is discoverable by binary search anyway — but it should be the operator's choice on unauthenticated endpoints, not an unconditional default. **Recommendation:** gate the 429 headers on `EmitRateLimitHeaders` too, or add `EmitRateLimitHeadersOnRejection` defaulting to true, so it is at least switchable.

#### ✅ DONE (documented) · P2-5 · `HashApiKey` truncates SHA-256 to 128 bits without a salt

`RateLimitPartitionKeys.HashApiKey` is correctly reasoned — this is a partition identifier, not a password hash, and collision resistance is the requirement. Two notes:

- **Truncation to 128 bits is fine** for collision resistance at any realistic key count.
- **`KnownApiKeyHashes` in a ConfigMap is a rainbow-table target for low-entropy keys.** An unsalted SHA-256 of `"acme-prod-key"` is trivially reversible. The docs must state that API keys used here **must be high-entropy random values**, not human-chosen strings. Today `RateLimitingOptions.cs:552` explains the format but not the entropy requirement.
- The `FrozenSet.Contains` lookup is not constant-time. Irrelevant here (partitioning, not authentication) and correctly framed as such in the XML docs — no action, noted so a future reader does not "promote" this to auth.

#### ✅ DONE · P2-6 · `ProcessHost` is correctly defaulted off but `AllowedHosts` is not enforced

`ForwardedHeadersSettings.ProcessHost` defaults to `false` with an excellent explanation (nginx-ingress forwards `$best_http_host`, which is client-supplied, so trusting the proxy does not make the header trustworthy). The doc comment says to pair it with `AllowedHosts`. Nothing enforces that. **Recommendation:** when `ProcessHost = true` and `AllowedHosts` is unset or `"*"`, fail Production startup with the same rigour applied to the trust set. This is `Request.Host` becoming caller-controlled, and `Host` feeds URL generation, redirect targets, password-reset links, and cache keys — the exact list already written in the XML doc. The package clearly knows the risk; it should guard it.

### P3 — OPTIONAL

- ✅ **P3-1** `Activity.Current?.SetTag("correlation_id", ...)` — non-semconv tag name. There is no OTel convention for an application correlation id, so this is defensible; consider `apipipeline.correlation_id` to namespace it and avoid colliding with another library's tag of the same name.
- ✅ **P3-2** `CorrelationIdSettings.AcceptInboundCorrelationId` defaults to `true`, so an internet-facing API lets callers choose the id that appears in its logs. Validated to `[A-Za-z0-9_.-]` and length-bounded, so injection and log-forging are handled; the residual risk is a caller deliberately colliding ids across requests to confuse an investigation. The doc comment covers it. Consider defaulting to `false` when `ASPNETCORE_ENVIRONMENT=Production` — or at minimum say in the README which of the two defaults an internet-facing API should pick.
- ✅ **P3-3** (documented) No `Cache-Control` default in `SecurityHeadersSettings`. An API that forgets to set it on an authenticated response can have it cached by an intermediary. The framework does not default it either, and the package's caching phases are auth-safe, so this is a documentation item rather than a code one.

---

## 7. PERFORMANCE FINDINGS

The performance work here is above average: `FrozenDictionary`/`FrozenSet` snapshots, precomputed policy signatures, `stackalloc` + `ArrayPool` in `HashApiKey`, a span-based validation scan, a reference-identity-keyed HSTS string cache, `static` lambdas on the `OnStarting` hot path, and an actual BenchmarkDotNet project that deliberately avoids `TestServer` so `[MemoryDiagnoser]` numbers mean something. Credit where due.

### Proven / significant

| # | Location | Current behaviour | Hot-path impact | Better implementation | Benchmark? |
|---|---|---|---|---|---|
| PERF-1 ✅ DONE | `SecurityHeadersMiddleware.cs:160` | `logger.LogDebug("Security headers applied to response")` on every response | One level check per response, plus an `ILogger` field carried through the boxed state tuple purely to reach it. At 10k RPS with Debug enabled it is 10k useless log records per second per pod. | Delete it | No — deletion is unconditionally correct |
| PERF-2 ✅ measured | `CorrelationIdMiddleware.InvokeAsync` | Per request: a boxed `(HttpContext, string, string?)` tuple for `OnStarting`, a `KeyValuePair[2]` for the log scope, the scope's own `IDisposable`, `Guid.NewGuid().ToString("N")` when generating | ~4 allocations/request from one middleware | The tuple and scope array are near-irreducible given the `OnStarting(Func<object,Task>, object)` and `BeginScope(object)` signatures — the comment at `:88` correctly explains why a custom `IReadOnlyList` struct was rejected. Measure before changing anything. | **Yes** — `CorrelationId_IdGenerated` already exists; commit a baseline |
| PERF-3 ✅ measured | `RateLimitPartitionKeys.ForUser/ForIp/ForApiKeyHash` and `EdgeRateLimitingMiddleware`'s `string.Create` | A fresh partition-key string on **every** request | 1–2 string allocations/request on the rate-limited path; `NormalizeIp` additionally allocates an `IPAddress` and its `ToString()` for IPv6 | The framework's own partitioners do the same, so this is table stakes rather than a defect. A per-connection cached key (`HttpContext.Connection.Id`-keyed, or `IHttpConnectionFeature`-attached) would remove it, at the cost of complexity that is hard to justify without a measurement. | **Yes** — no rate-limiting benchmark exists at all. Add one. |
| PERF-4 ✅ done | `CorrelationIdMiddleware`, `RequestSizeMiddleware` registered as `IMiddleware` via `TryAddTransient` | Per request: `IMiddlewareFactory.Create` + a transient DI resolution + `Release` | Small but strictly avoidable — convention-based middleware is instantiated once | Convention-based (`RequestDelegate next` constructor), as `SecurityHeadersMiddleware` and `EdgeRateLimitingMiddleware` already are. The inconsistency has no stated rationale; `AddApiPipelineCorrelationId`'s doc comment says the DI registration is "required for the `IMiddleware` activation pattern", which is circular. | **Yes** — `CorrelationId_*` benchmarks exist; add a convention-based variant and compare |

### Benchmark required before acting

| # | Location | Question |
|---|---|---|
| PERF-5 ✅ decided + documented | `ApiPipelineBuilder` phase order: `ResponseCompression` (11) **before** `ResponseCaching` (12) / `OutputCaching` (13) | Compression is *outer* to the caches, so the cache stores one uncompressed entry and **every cache hit re-compresses**. The alternative caches compressed bytes (CPU paid once) but needs `Vary: Accept-Encoding` correctness and multiplies cache entries per encoding. `docs/ARCHITECTURE.md:67` states the current choice and its reason; `docs/features/response-compression.md:42,174` states the **opposite** (§8). Decide, measure at a realistic hit rate, and make the docs agree. |
| PERF-6 ✅ evaluated + documented | `ConfigureResponseCompressionOptions.Configure` | `ResponseCompressionDefaults.MimeTypes.Concat([...]).Distinct(...).ToList()` then `RemoveAll` then `ToArray`. Startup-only, so the LINQ is free — but `options.MimeTypes` ends as a `string[]` that `ResponseCompressionProvider` scans linearly per response. A `FrozenSet` would be better if the list is long. Almost certainly noise; measure before touching. |

### Theoretical / no action

- `CorrelationHeaders.All` allocates a fresh array per read — called only during CORS policy construction (startup and reload). The defensive-copy rationale in the XML doc is correct.
- `RateLimiterRegistration`'s `cachedResolver ??=` closure is a non-volatile shared reference field. Worst case two first-concurrent-requests resolve the same singleton twice and one is discarded — the comment says exactly this. Using `Volatile.Write`/`LazyInitializer` would be tidier; the behaviour is already correct.
- `SecurityHeadersMiddleware`'s `ContainsKey` guard before each header write: 6 lookups per response on a small header collection. Required for the documented override semantics.

### Missing from the benchmark suite

`docs/performance.md` lists six scenarios, all on correlation-id / security-headers / request-size. There is **no benchmark for the rate limiter, CORS policy resolution, or the full pipeline under concurrency** — which is where the interesting costs and the only lock contention live. And no baseline numbers are committed. The stated reason ("comparing a number from someone else's laptop to yours is worse than having no number at all") is fair for absolute latency but wrong for *ratios*: a committed `allocations/op` figure per middleware would catch a regression in review, which is the whole point. **Recommendation:** commit allocation counts (deterministic across machines) even if you never commit timings.

---

## 8. MAINTAINABILITY FINDINGS

Ranked by likelihood of becoming a maintenance nightmare.

1. ✅ **DONE.** **`VersioningServiceCollectionExtensions` (111 lines) + `VersionReportingStartupWarning` (106 lines) + `DeprecationReportingTelemetry` (61 lines) = 278 lines to record one counter.** Manual `ServiceDescriptor` removal and re-registration, a three-way descriptor materialiser, a private marker type for idempotency, a scoped resolution from a singleton `IHostedService` to avoid breaking container validation, and a documented "must be called after every `IReportApiVersions` registration" ordering constraint. Every one of those is a correct solution to a real problem, and the aggregate is the most fragile thing in the repo. **Delete the metric or take `Scrutor`.**

2. ✅ **DONE.** **The `__EndpointRouteBuilder` dependency.** See P2-2. This is a scheduled maintenance event on every .NET major.

3. ✅ **DONE.** **Two public APIs for the same thing.** 12 `Use*` methods **and** `UseApiPipeline(pipeline => ...)`; 15 `Add*` methods **and** `AddApiPipeline(configuration, configure)`. Both are public, documented, and tested, so both must keep working forever. Worse, the individual `Use*` path has **none** of the ordering guarantees that are the package's reason to exist — a consumer who wires `Use*` calls by hand gets the phase enum's protection only via `PipelineOrderGuard`'s narrow routing check. **Recommendation:** make the individual `Use*` methods `internal`, or mark them `[Obsolete("Use UseApiPipeline(...) — individual Use* calls do not enforce phase ordering.")]` in v2.0.0 while the door is open. Shipping v2 with both is committing to both.

4. ✅ **DONE.** **Comment-to-code ratio.** `ApplicationBuilderExtensions.cs` is 592 lines of which a large fraction is prose. The prose is *good* — it explains framework behaviour a reader cannot infer — but it is also a signal: `UseApiPipelineForwardedHeaders` needs 40 lines of comment because it does five things (parse, validate, count, guard, branch). Extract `ForwardedHeadersConfigurator` as its own type and the comments become type-level documentation instead of inline archaeology.

5. ✅ **DONE.** **`AddApiPipeline` has three registration tiers with three different rationales.** Flag-gated (9 features), unconditional-with-a-comment (output caching, edge rate limiting — "gating on a code-only flag means an operator can set Enabled=true and get no signal why"), and unconditional-for-hardening (Kestrel). The reasoning for each is written down and each is individually right. Collectively it means a consumer cannot predict from `ApiPipelineServiceRegistrationOptions` what actually got registered. **Recommendation:** drop `ApiPipelineServiceRegistrationOptions` entirely and register everything unconditionally — every registration is already inert until its own `Enabled` is true, which is the argument the code itself makes for output caching. That deletes a public type, nine flags, and a whole class of "why didn't my setting apply" support tickets.

6. ✅ **DONE** (now `RateLimitPartitioningOptions`). **`ServiceCollectionExtensions.cs:129-141`: a manual `int.TryParse` of one raw configuration key** to work around the binder appending to `List<RateLimitPolicy>`. Correct, well-explained, and structurally wrong — a shared setting read by two features should be its own options type, not a scalar smuggled across a `Configure` callback.

7. ✅ **DONE.** **`docs` contains a factual contradiction.** `docs/ARCHITECTURE.md:67` — "Outside the caches, so the cache stores one **uncompressed** entry"; `docs/features/response-compression.md:42` — "caching after compression stores the **compressed** form"; `:174` — "caching works with compression (stores compressed form)". The code puts compression outer to the caches, so `ARCHITECTURE.md` is right and `response-compression.md` is wrong in two places. Fix the feature doc.

8. ✅ **DONE.** **`docs/architecture-review.md`** describes files that no longer exist (`Middleware/ApiVersionDeprecationMiddleware.cs`). It carries a "historical document" banner, which is the right call — but a stale review in `docs/` will be read as current by someone. Move it to `docs/archive/`.

---

## 9. OBSERVABILITY FINDINGS

### Done right — call this out

- **No custom `ActivitySource`.** The package starts zero spans and says so explicitly in `ApiPipelineTelemetry`'s XML docs: ASP.NET Core's instrumentation already produces the server span, and a span per middleware phase would add cost without information. This is the correct decision and most internal packages get it wrong.
- **No "requests processed" counter**, with the reason stated: `http.server.request.duration` already carries that count with more dimensions.
- **`http.server.request.body.size` uses the semconv name**, and the claim that ASP.NET Core does not emit it is **verified correct** on `Microsoft.AspNetCore.App 10.0.0`.
- **Cardinality discipline is real and tested.** Route *templates* never raw paths; `partition_type` from a fixed four-value set; `correlation_id` deliberately **not** a metric dimension (`RecordCorrelationIdRejected` is untagged with the reason written down). `TelemetryCardinalityTests` (215 lines) and `MetricNamingTests` enforce it. This is better than most production observability code.

### Duplicated telemetry

- **`apipipeline.ratelimit.rejected` overlaps `aspnetcore.rate_limiting.requests`** (verified present, with `result` and `policy` tags). Only `partition_type` is additive. See §4.2.

### Missing telemetry

- **No signal when rate limiting silently turns itself off** (P1-2). `GetNoLimiter("no-policy")` and `GetNoLimiter("no-policy")` in the named-policy path both mean "this security control is not running" and neither is observable.
- **No signal when a configuration reload is rejected.** After P1-1's fix, "we are running on stale configuration" must be a metric or an error log, not silence.
- **No signal when `PipelineOrderGuard` becomes inert** (P2-2).
- **Edge-limiter rejections produce no framework metric** (P2-3) — the one place where a custom counter is genuinely load-bearing, and it currently shares an instrument with the framework-covered path.

### Unnecessary logging

- `LogDebug("Security headers applied to response")` on every response (PERF-1).
- `EdgeRateLimitingMiddleware`'s per-rejection `LogDebug` is invisible at production log levels, so during a flood there is effectively no log signal at all — while the same event at `Information` without sampling would be a log flood. Neither extreme is right; make it a metric and drop the log.

### High-cardinality telemetry

None found. `TelemetryCardinalityTests` covers it.

---

## 10. MICROSOFT ALIGNMENT REPORT

| Capability | Current approach | Microsoft recommended | Aligned? | Required change |
|---|---|---|---|---|
| Rate limiting | `AddRateLimiter` + `UseRateLimiter` + `PartitionedRateLimiter` from `System.Threading.RateLimiting` | Same | ✅ | Fail-closed on unresolved policy (P1-2) |
| Edge (pre-auth) rate limiting | Custom middleware over `PartitionedRateLimiter` | No framework support for a second policy-less limiter | ✅ (justified) | Unify rejection shape (P2-3) |
| Output caching | `AddOutputCache` + `UseOutputCache` | Same | ✅ | — |
| Response caching | `AddResponseCaching` + `UseResponseCaching`, and startup fails if both caches are on | Same; MS also recommends output caching over response caching | ✅ **exceeds** | — |
| Response compression | `AddResponseCompression` + `UseResponseCompression` behind `UseWhen`, `EnableForHttps` off by default with a BREACH warning | Same; MS documents the HTTPS risk | ✅ **exceeds** | Resolve the doc contradiction (§8.7) |
| CORS | `AddCors` + `UseCors`, but `ICorsPolicyProvider` **replaced** | `AddCors` + named/default policies | ⚠️ partial | Fail-fast on a conflicting application default in Production (P2-1) |
| Forwarded headers | `ForwardedHeadersOptions` + `UseForwardedHeaders`, gated on a non-null peer address, with a Production trust-set guard | `UseForwardedHeaders` with `KnownProxies`/`KnownNetworks`; MS documents the empty-trust-set behaviour | ✅ **exceeds** | Use `IPNetwork.TryParse` (P1-3) |
| Security headers | Custom middleware (HSTS + nosniff + Referrer-Policy + X-Frame-Options + CSP + Permissions-Policy) | `UseHsts()` for HSTS; no framework middleware for the rest | ⚠️ partial | Delegate HSTS to `UseHsts()` or document the re-implementation (§3) |
| HSTS | Custom, with loopback + excluded-host + `RequireHttps` logic mirroring `HstsMiddleware` | `UseHsts()` | ⚠️ re-implemented | As above |
| HTTPS redirection | `app.UseHttpsRedirection()` | Same | ✅ | — |
| Request timeouts | `AddRequestTimeouts` + `UseRequestTimeouts` + `IConfigureOptions<RequestTimeoutOptions>`; per-endpoint overrides left to `WithRequestTimeout`/`DisableRequestTimeout` | Same | ✅ **exemplary** | — |
| Kestrel / request limits | `IConfigureOptions<KestrelServerOptions>` from validated options; `MinRequestBodyDataRate`, `RequestHeadersTimeout`, `KeepAliveTimeout`, `MaxConcurrentConnections` all covered | Same | ✅ **exceeds** (slowloris coverage is better than most) | — |
| Form limits | `AddOptions<FormOptions>().Configure<IOptions<RequestLimitsOptions>>` | Same | ✅ | — |
| Exception handling / ProblemDetails | **Not implemented** — delegated to `AddProblemDetails` + `UseExceptionHandler`, documented in 5 places, kept compiling by a test | `AddProblemDetails` + `UseExceptionHandler` | ✅ **correct non-implementation** | — |
| API versioning | `Asp.Versioning` + `IConfigureOptions<ApiVersioningOptions>` supplying policy values only; framework writes the headers | `Asp.Versioning` | ✅ **exemplary** — v2.0.0 deleted a custom middleware and version reader to get here | Drop the telemetry decorator (§4.3) |
| Options / configuration | `AddOptions<T>().Bind().ValidateDataAnnotations().ValidateOnStart()`, `IValidatableObject` for cross-field rules | Same | ✅ | Last-known-good on reload (P1-1) |
| Metrics | `System.Diagnostics.Metrics.Meter`, plugged in via `ConfigureOpenTelemetryMeterProvider` | Same | ✅ | Drop the duplicated counter (§4.2) |
| Tracing | No `ActivitySource`; tags the ambient activity | `AddAspNetCoreInstrumentation` owns the server span | ✅ **exemplary** | — |
| Logging | `ILogger` only | Same | ✅ | — |
| Middleware ordering | Phase enum; routing left to `WebApplication`'s auto-insertion | MS documents a recommended order | ✅ mostly — caches after auth is a deliberate, documented tightening; compression outer to caches is undocumented/contradicted | Document the compression choice (PERF-5) |
| DI | `TryAdd*`, `TryAddEnumerable`, `IConfigureOptions<T>` | Same | ✅ | — |

---

## 11. DEPENDENCY REPORT

| Package | Purpose | Necessary? | Better alternative? | Recommendation |
|---|---|---|---|---|
| `Microsoft.AspNetCore.App` (FrameworkReference) | All ASP.NET Core surface | Yes | None — this is the correct mechanism for a library, versus pulling individual `Microsoft.AspNetCore.*` packages | **Keep** |
| `Asp.Versioning.Http` (10.2.1) — Versioning package only | `ApiVersionParser`, `ApiVersioningOptions.Policies`, `IReportApiVersions` | Yes | None. Microsoft's own recommended versioning library. | **Keep** — and note that v2.0.0 dropped `Asp.Versioning.Mvc` from the shipped package, keeping it test-only. Correct call. |
| `OpenTelemetry.Extensions.Hosting` (1.18.0) — OTel package only | `ConfigureOpenTelemetryMeterProvider` | Yes | None | **Keep** — correctly isolated in a satellite package so core has zero OTel dependency |
| `CodeStyle.NET` (internal, `PrivateAssets="all" ExcludeAssets="runtime"`) | Analyzers | Yes | — | **Keep** — CI verifies it never leaks into the nupkg dependency list |
| `Microsoft.SourceLink.GitHub` (`PrivateAssets="All"`) | Source Link | Yes | — | **Keep** |
| `Scrutor` | *not referenced* | — | Would replace ~40 lines of `ServiceDescriptor` surgery | **Consider** — or delete the decorator instead (§4.3). Prefer deletion. |
| `Microsoft.AspNetCore.HeaderPropagation` | *not referenced* | — | Would replace `CorrelationIdPropagationHandler` | **Consider** — official Microsoft package for exactly this. The custom handler is only ~20 lines, so this is close to a wash; if it stays custom, say why in the docs. |
| `NetEscapades.AspNetCore.SecurityHeaders` | *not referenced* | — | Would replace most of `SecurityHeadersMiddleware` | **Evaluate honestly.** The package's early-phase requirement is satisfiable with either. 202 lines of security-header code that duplicates `HstsMiddleware` is 202 lines the team owns forever. |
| `FluentAssertions [7.1.0]` (test only, exact-pinned) | Assertions | Yes | — | **Keep** — the exact-version pin correctly avoids the v8 licence change |
| `BenchmarkDotNet`, `coverlet.collector`, `xunit*`, `Microsoft.NET.Test.Sdk`, `Microsoft.AspNetCore.{TestHost,Mvc.Testing}` | Test/perf | Yes | — | **Keep** |

**Verdict: dependency hygiene is the strongest area of this repository.** Three runtime dependencies across three packages, all official, all correctly scoped. CPM, `NuGetAudit` at `low`/`all`, `packageSourceMapping` (dependency-confusion defence), actions pinned to full SHAs with Dependabot maintaining both SHA and comment, deterministic builds, `ContinuousIntegrationBuild`, Source Link + embedded PDB, and a CI step that unzips every nupkg to assert no private-feed dependency leaked. That last one is a control most teams never build.

The only gap: `PackageValidationBaselineVersion` is opt-in behind `vars.API_COMPAT_BASELINE_VERSION`, which is empty. **API compatibility checking is currently off.** For a package consumed by 30 applications that is the wrong default — set the variable to `1.x` before publishing v2.0.0 so the intended breaks are reviewed rather than discovered.

---

## 12. TOP 10 CHANGES

Ranked by security → performance → maintenance reduction → simplification → observability.

| # | Change | Type | Effort | Why it ranks here |
|---:|---|---|---|---|
| 1 | ✅ **DONE** — **Last-known-good snapshots for every hot-reloaded options type**, plus a guarded reload that never throws into the watcher thread (P1-1) | Security/Reliability | ~4h + 4 tests | Removes a verified ConfigMap-typo-to-total-outage path across every consuming app. The package already has the pattern in three places. |
| 2 | ✅ **DONE** — **Fail closed, and observably, when a rate-limit policy cannot be resolved** (P1-2) | Security | ~2h | A live config edit can currently switch rate limiting off with zero signal on a pod passing every probe. |
| 3 | ✅ **DONE** — **Delete both hand-rolled CIDR parsers; use `IPNetwork.TryParse`** (P1-3, §4.1) | Security + maintenance | ~30m | The trust boundary is currently guarded by a second, independent copy of the parse it guards. −55 lines. |
| 4 | ✅ **DONE** — **Fail startup (Production) on a conflicting application CORS default, and on `ProcessHost=true` without `AllowedHosts`** (P2-1, P2-6) | Security | ~3h | Two places where the package identifies a serious risk in a doc comment and then only warns. Warnings are not guardrails at 30-app scale. |
| 5 | ✅ **DONE** — **Delete the version-deprecation telemetry decorator, its startup warning, and its metric** (§4.3) | Maintenance | ~2h | 278 lines and the repo's most fragile code, in service of one counter. Removes a public ordering constraint and a container-surgery block. |
| 6 | ✅ **DONE** — **Make individual `Use*`/`Add*` methods `internal` or `[Obsolete]`; delete `ApiPipelineServiceRegistrationOptions`** (§8.3, §8.5) | Simplification | ~4h | Halves the public surface being committed to, and eliminates the path that gets none of the ordering guarantees the package exists to provide. v2.0.0 is the only window. |
| 7 | ✅ **DONE** — **Reconcile `apipipeline.ratelimit.rejected` with the verified `aspnetcore.rate_limiting.requests`; give the edge limiter its own instrument; unify the two rejection shapes** (§4.2, P2-3) | Observability | ~3h | During a flood — when it matters — the edge path currently produces no framework metric and an invisible Debug log. |
| 8 | ✅ **DONE** — **Make `PipelineOrderGuard` self-diagnosing about the framework-internal key** (P2-2) | Maintenance | ~2h | Converts a scheduled silent failure on the next .NET major into a logged one. |
| 9 | ✅ **DONE** — **Add rate-limiter and full-pipeline-under-concurrency benchmarks; commit allocation baselines** (§7) | Performance | ~4h | The costliest and most contended path has no benchmark, and no committed baseline means no regression can be caught in review. |
| 10 | ✅ **DONE** — **Turn on API compatibility validation; delete the per-response `LogDebug`; fix the compression/caching doc contradiction; archive the stale review** (§11, PERF-1, §8.7, §8.8) | Hygiene | ~2h | Cheap, and #10's first item is the control that protects 30 consumers from an accidental break. |

Deliberately **not** ranked: renaming anything, splitting `ApplicationBuilderExtensions`, adding abstractions or extensibility points, or reducing the comment volume. None of those change security, performance, or the maintenance obligation.

---

## 13. MAINTENANCE DEBT SCORE

### Maintenance Risk: **MEDIUM** at review → **LOW–MEDIUM** now

Not LOW, because of three specific commitments. Not HIGH, because the custom surface is small, deliberately chosen, and — unusually — *explained in the code*.

> **What moved it.** The table below is unchanged as an inventory: every framework-internal
> assumption listed is still one this package depends on. What changed is that each now has a
> named entry in `CONTRIBUTING.md`'s .NET-upgrade checklist, the `__EndpointRouteBuilder` row
> reports at runtime when it has gone inert, the `IReportApiVersions` descriptor-surgery row is
> **deleted outright**, and API compatibility validation now catches an accidental break to any of
> the public surface below. The two rows this section called out as the *dangerous* part — "two
> public APIs for every operation" and the 258-member surface — are the ones addressed by §8.3 and
> §8.5, though the deprecated `Use*` methods remain for one major version.

**What the team is signing up to maintain:**

| Committed liability | Ongoing cost | Trigger |
|---|---|---|
| `"__EndpointRouteBuilder"` | Verify on every .NET major | .NET 11, 12, … |
| `RateLimitPolicySignature` + partition-key scoping | Re-verify that `RateLimitPartition`'s options factory still caches per key | Any `System.Threading.RateLimiting` change |
| `DenyAllRateLimiter` | Re-verify that `FixedWindowRateLimiter` still rejects `PermitLimit = 0` | Any `System.Threading.RateLimiting` change |
| `GlobalLimiter` deference to endpoint policies | Re-verify that ASP.NET Core still evaluates global **and** endpoint limiters | Any `Microsoft.AspNetCore.RateLimiting` change |
| `LiveConfigCorsPolicyProvider` | Track `CorsPolicyBuilder` / `CorsService` behaviour, including the `IsOriginAllowed` delegate wrapping and the "wildcard short-circuits before the delegate" assumption | Any `Microsoft.AspNetCore.Cors` change |
| `SecurityHeadersMiddleware` HSTS logic | Keep in sync with `HstsMiddleware`'s loopback/exclusion/transport semantics | Any RFC 6797 guidance or `UseHsts` change |
| `IReportApiVersions` descriptor surgery | Track `ServiceDescriptor` shapes and `Asp.Versioning`'s registration | Any `Asp.Versioning` major |
| `ForwardedHeadersMiddleware`'s `checkKnownIps` behaviour | The whole Production trust guard depends on this internal detail continuing to work as documented | Any `Microsoft.AspNetCore.HttpOverrides` change |
| **Two public APIs for every operation** | Every future feature must be exposed twice and tested twice | Every feature |
| **258 public members across 3 packages** | Every one is a semver commitment to 30 apps | Every release |

**Answering the ownership questions directly:**

- *Security vulnerability in two years?* The team owns `SecurityHeadersMiddleware`, `LiveConfigCorsPolicyProvider`, `NormalizeIp`, `HashApiKey`, and the partitioning logic. That is a genuine but bounded AppSec surface, and the test suite is organised by security guarantee, which is what makes it maintainable.
- *ASP.NET Core changes behaviour?* Eight tracked framework-internal assumptions above. Each has a test. One (`__EndpointRouteBuilder`) fails silently in production if the test is not run against the preview.
- *Performance degrades at 10,000 RPS?* Nobody owns this today — there is no rate-limiter benchmark, no concurrency benchmark, and no committed baseline. This is the biggest gap between the repo's stated priorities and its actual controls.
- *Kubernetes networking changes assumptions?* Well covered. `ForwardLimit`, UDS/null-peer, ConfigMap reload, probe exclusions, and process-local limiter semantics are all handled and documented.
- *Breaking change across 30 apps?* Config section names are stable, `docs/migration.md` is 328 lines, `CHANGELOG.md` is 36KB — but API compatibility validation is **currently disabled**. Turn it on.

**The debt is manageable and mostly deliberate.** The dangerous part is not the custom code — it is that the public surface is twice as large as it needs to be, and v2.0.0 is the last cheap chance to halve it.

---

## 14. FINAL ARCHITECTURAL ASSESSMENT

**Is ApiPipeline.NET primarily a thin standardization/orchestration layer?**
**YES.** ~3,500 lines of source across three packages, of which the substantial custom behaviour is rate-limit partitioning, the phase orderer, the forwarded-headers guard, and the CORS provider. Everything else registers, validates, or configures framework components. v2.0.0 moved further in this direction by deleting a custom version-deprecation middleware, a custom `IApiPipelineVersionReader`, and a custom `ApiVersionMatch` in favour of `Asp.Versioning` policies.

**Is it unnecessarily becoming a custom framework?**
**NO** — with one qualification. The phase builder is the closest thing to a framework here, and it earns its place: the four ordering invariants it protects are exactly the mistakes that cost production incidents. The qualification is that shipping *both* the builder and 27 individual `Add*`/`Use*` methods doubles the surface without doubling the value, and the individual path bypasses the guarantees.

**Is it reinventing functionality already available from Microsoft?**
**YES, in three narrow places** — the CIDR parser (`IPNetwork.TryParse`, verified), the HSTS logic (`UseHsts`), and the rate-limit rejection counter (`aspnetcore.rate_limiting.requests`, verified). All three are small and all three are removable. Everywhere else the answer is a clear no, and in several places (exception handling, tracing, request timeouts, versioning) the package explicitly declines to build something and documents why.

**Is it reinventing functionality available from mature industry packages?**
**PARTIALLY.** `NetEscapades.AspNetCore.SecurityHeaders` covers most of `SecurityHeadersMiddleware`; `Scrutor` covers the decorator surgery; `Microsoft.AspNetCore.HeaderPropagation` covers the correlation-id propagation handler. None of the three was evaluated in writing. For an org-wide package, "we chose not to take this dependency, because X" belongs in `docs/ARCHITECTURE.md`.

**Is custom code minimized?**
**MOSTLY YES.** ~350 lines are removable today with no loss of function (§4). The remainder is either justified by a documented framework gap or is the package's actual value.

**Are dependencies minimized?**
**YES.** Three runtime dependencies, all official, correctly scoped into satellite packages so core has zero. This is the best area of the repo.

**Is Security treated as a first-class requirement?**
**YES in design, NO in one execution detail.** Secure-by-default across the board — CORS off, compression-over-HTTPS off, `ProcessHost` off, `AnonymousFallback = Reject`, API-key partitioning requiring an allow-list, HSTS self-suppressing in Development and over non-verified transports, the Production trust-set guard. That is a genuinely security-led design. But two security controls can currently be disabled at runtime without a signal (P1-2), and two identified risks are only warned about rather than guarded (P2-1, P2-6).

**Is Performance treated as a first-class requirement?**
**MOSTLY YES.** The right data structures, the right allocation avoidance, the right decision to precompute at startup, and a benchmark project that measures the right thing. But no baselines are committed and the hottest, most contended path — rate limiting — has no benchmark at all.

**Is Maintainability treated as a first-class requirement?**
**PARTIALLY.** Excellent: comments that explain framework behaviour, tests organised by guarantee, a test that fails when a framework-internal key is renamed, and a v2.0.0 that deleted code rather than adding it. Poor: two public APIs for everything, 258 public members, 278 lines to record one counter, a duplicated parser at the trust boundary, and API compatibility validation switched off.

**Is Readability/Simplicity treated as a first-class requirement?**
**PARTIALLY.** Terminology is framework-native throughout (`Add*`/`Use*`, `IMiddleware`, `IConfigureOptions`, options pattern) — no ApiPipeline-specific vocabulary invented where Microsoft has words. A senior .NET developer reads this without a glossary. But `ApplicationBuilderExtensions.cs` at 592 lines with a very high comment ratio is comprehensible rather than simple, and the duplicated public API means a newcomer has to work out which of two ways is the intended one.

**Is Observability standards-based?**
**YES.** `Meter` and `ILogger` only; no `ActivitySource`; semantic-convention names where they exist; cardinality enforced by tests. The single deviation is one counter that overlaps a framework metric.

---

## FINAL QUESTION

> **If you personally owned production support for 30 applications using this package for the next five years, would you approve the current architecture and implementation?**

## **YES — approve.**

> **The answer as first written was "NO — but only just, and only until three fixes land."** All
> three landed, with a test per affected reader, and the fix for the worst of them is better than
> the one this review proposed. Every P2 and P3 finding closed alongside them, plus two defects this
> review did not catch. See [Resolution](#resolution) for what was done and how it was verified.
>
> The original reasoning is kept below, unchanged and in its original tense, because it is the
> argument that produced the fixes — and because the two paragraphs that follow are still the
> honest case for and against owning this package.

I would approve the **architecture** today without reservation. The build-vs-buy judgement is better than most internal platform packages: it delegates every piece of infrastructure to the framework or an official Microsoft library, keeps three runtime dependencies, publishes no observability platform of its own, refuses to build exception handling, and in v2.0.0 *deleted* custom code in favour of `Asp.Versioning` policies. The phase orderer and the forwarded-headers trust guard are exactly the two things a shared pipeline package should own, and both are better than what 30 teams would each write. The dependency and supply-chain hygiene is the best I have seen in an internal package.

I would not have signed for **support** on the implementation as it stood, for one reason: I would be the person paged when someone edits a ConfigMap and an application returns 500 to every caller while its liveness probe stays green. That failure is verified, reachable by a one-character edit, invisible to the health checks the package's own docs recommend, and made *more* likely by the fact that hot-reload is advertised as a feature. Alongside it, two security controls — rate limiting and the CORS policy — can stop working at runtime with no signal at all. In a 30-application blast radius, "we logged a warning" is not an answer I can give an incident review.

Three fixes change my answer:

1. **Last-known-good snapshots for every hot-reloaded options type**, with a guarded reload that cannot throw into the config-watcher thread, and a test per reader (P1-1).
2. **Fail closed and observably when a rate-limit policy cannot be resolved** (P1-2).
3. **One CIDR parser, `IPNetwork.TryParse`, at the trust boundary** (P1-3).

Add the two fail-fast promotions (P2-1, P2-6) and I would recommend it actively rather than merely approve it. Do #5 and #6 from the Top 10 while v2.0.0 is still unpublished and it becomes a package I would hold up as the example of how to build this layer.

The gap between this and an approval is roughly two days of work, and none of it is architectural.

---

> **Closing note.** All three fixes landed, and so did both fail-fast promotions and #5 and #6 from
> the Top 10 — while v2.0.0 was still unpublished, which was the window this review said to use.
> By its own standard, that makes this a package to recommend actively rather than merely approve.
