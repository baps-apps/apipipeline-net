# ApiPipeline.NET 2.0.0 — development notes

The detailed change log written while 2.0.0 was developed, moved here from `CHANGELOG.md` so the
changelog itself stays user-facing. Kept for the reasoning behind each change. Written in the
present tense of each review; names in it predate the final 2.0.0 API (for example the option-type
renames to `ApiPipeline*Options`, the move of the extension methods to Microsoft's namespaces, and the
IETF `RateLimit-Policy` headers replacing `X-RateLimit-*`, all of which came last). For the 2.0.0
release as shipped, read [CHANGELOG.md](../../CHANGELOG.md) and [migration.md](../migration.md).

## Development log

> This release contains **breaking API changes**. See [docs/migration.md](docs/migration.md) for the
> upgrade steps. Four groups of changes are recorded below: the second production-readiness review's
> follow-up first, then the first one's, then the architecture-review follow-up, then the original
> 2.0.0 work.

### Second production readiness review follow-up (2026-09-02)

> A second full principal-architect / AppSec / performance / SRE pass over the same working tree,
> run under the "do not reinvent the wheel" brief. It found two defects the first review missed —
> both in CORS, both verified by running code against the built assembly rather than by reading it —
> and both traceable to the same root cause: **every default in this repository was asserted on a
> freshly constructed options object, so no test ever put `IConfiguration` in the loop.**

#### Fixed

- **A CORS method or header allow-list configured in `appsettings` could be widened but never
  narrowed.** `CorsSettings.AllowedMethods` and `AllowedHeaders` carried their default arrays as
  property initialisers, and `ConfigurationBinder` **appends** to a collection that already has a
  value rather than replacing it. Configuring `"AllowedMethods": ["GET"]` therefore produced an
  effective allow-list of `GET, POST, PUT, PATCH, DELETE, OPTIONS, GET`.

  An operator who deliberately restricted an API to read-only cross-origin access still allowed
  `POST`, `PUT`, `PATCH` and `DELETE` from the browser, and no signal anywhere said so. With
  `AllowCredentials: true` and a cookie-authenticated browser client that is a materially wider
  CSRF surface than the configuration declared.

  Both properties now default to `null`, with the values in `CorsSettings.SafeDefaultAllowedMethods`
  and `SafeDefaultAllowedHeaders` applied when nothing is configured — the shape this repository
  already used for `SecurityHeadersSettings`' loopback exclusions and for
  `RateLimitPartitioningOptions`, both of which document this exact binder behaviour. A configured
  list now replaces the defaults outright. The startup validators accept an absent list and still
  reject one that was configured but holds nothing usable.

- **`CorsOptions:Enabled=false` turned CORS off entirely instead of handing it back to the
  application.** `LiveConfigCorsPolicyProvider` is registered with `Replace`, so it owns the host's
  default (unnamed) CORS lookup whether the feature is on or not — and when disabled it answered
  that lookup with `null`. An application that set `Enabled: false` specifically to keep its own
  `AddCors(o => o.AddDefaultPolicy(...))` got **no CORS policy at all** and every cross-origin call
  failed. That was the documented remedy in three places (the feature guide, `AddApiPipeline`'s XML
  doc, and `CorsOverrideStartupGuard`'s own message), so the guard was advising the outage it exists
  to prevent.

  The provider now delegates the default lookup to ASP.NET Core's `DefaultCorsPolicyProvider` when
  the feature is disabled, restoring exactly the behaviour the host would have had without this
  package. `CorsOverrideStartupGuard` no longer reports anything in that case, because there is no
  longer an ambiguity to report.

- **Mixing the aggregate and per-feature registrations failed host start.** `AddApiPipeline(config)`
  plus any `AddApiPipeline<Feature>(config)` — both documented, both looking correct — bound the
  same configuration section twice. Because the binder appends, the second bind duplicated every
  entry in `RateLimitingOptions.Policies` and the host died with `Duplicate policy name 'strict'`, a
  message that names neither of the two calls responsible. `RateLimiterOptions.AddPolicy` would have
  thrown for the duplicate name too. Every binding registration now claims its options type on
  first call and is a no-op afterwards; first call wins.

- **The `/health` exclusions could not be removed.** `EdgeRateLimitingOptions.ExcludedPaths` and
  `ResponseCompressionSettings.ExcludedPaths` carried `["/health"]` as property initialisers, with
  the same append behaviour: a deployment whose probe is not at `/health` could add its path but
  never drop the one it does not use, and no deployment could opt out of the exclusion. Both now
  default to `null` with the value in `DefaultExcludedPaths`.

- **Hot reload followed the wrong configuration.** `ValidatedSnapshot<T>` listened to the host
  `IConfiguration`'s reload token, not the configuration that was passed to `AddApiPipeline*`. An
  application binding from a configuration it built itself got no hot reload at all, silently. It now
  listens to the options' own `IOptionsChangeTokenSource<T>`, as `OptionsMonitor` does.

- **The `ProcessHost` guard misjudged code-configured host filtering.** It read the raw
  `AllowedHosts` configuration key, so an application that set `HostFilteringOptions.AllowedHosts`
  in code failed Production startup despite being safe, and one that reopened it to `"*"` in code
  over a configured list passed. It now judges the effective `HostFilteringOptions`.

- **The build could not restore the API-compatibility baseline.** `Nuget.config`'s package source
  mapping sent `ApiPipeline.NET` only to nuget.org, where it is not published, so every `dotnet
  build` failed with `NU1101` once validation was turned on. It is now mapped to the private feed.

#### Changed

- **`RateLimitingOptions:EmitRateLimitHeadersOnRejection` now defaults to `false`** (`true` in 1.x
  and in the previous 2.0.0 pre-release). `X-RateLimit-Limit` and `X-RateLimit-Window` on a `429`
  hand an unauthenticated caller the exact quota to calibrate a flood against, and the edge limiter
  rejects *before* authentication, so they cannot be sent selectively. A package deployed into
  anonymous, authenticated, internal and internet-facing applications alike cannot know which it is
  in, so the disclosing option is the wrong one to choose on the consumer's behalf. An API with known
  callers opts in. `Retry-After` and `Cache-Control: no-store` are sent either way.

- **The rate-limit user partition reads `ClaimTypes.NameIdentifier` first**, then `sub`, then
  `nameid`. The JWT handler's default `MapInboundClaims` remaps `sub` onto `NameIdentifier`, so the
  old order walked the claims collection twice for nothing on the common configuration — and a token
  transformation leaving a `sub` claim that is not the subject would have partitioned two users into
  one bucket.

- **The "compression enabled but not for HTTPS" message dropped from `Warning` to `Information`.**
  It is the *safe* branch; the BREACH/CRIME warning on the other branch is the one that matters, and
  logging both at `Warning` made the real warning indistinguishable from routine noise.

#### Added

- **One `Information` line at startup naming the composed pipeline phases in execution order**
  (`ApiPipeline.NET pipeline composed, in execution order: [...]`). Two phases logged their own
  "enabled" line and the other twelve logged nothing, so the commonest question about this package —
  "why is *phase* not applying?" — could only be answered by reading `Program.cs` alongside
  configuration. It is now answerable from a pod log.

- **`ConfigurationBindingTests`** — the test class the two CORS defects existed for want of. Every
  collection-valued option is now asserted *after* real configuration has been bound through the
  real registration method, and the aggregate/per-feature and repeated-registration combinations are
  pinned. **Any new collection-valued option needs a case here.**

- **`CorsProviderOwnershipTests`** — who answers the default CORS lookup with the feature on and
  with it off, so the documented opt-out cannot silently stop working again.

#### Removed

- **`ApiPipeline.NET.OpenTelemetry` (the whole package).** Its entire body was one call:
  `metrics.AddMeter(ApiPipelineTelemetry.MeterName)`. A NuGet package with its own version number,
  API-compatibility baseline and release cadence is a poor trade for a line of code the consuming
  application can read — and `AddApiPipelineTelemetrySources()` hid *which* meter was being
  registered behind a name that did not say. Name the meter in the OpenTelemetry bootstrap the
  application already owns:

  ```csharp
  .WithMetrics(m => m.AddMeter(ApiPipelineTelemetry.MeterName))   // "ApiPipeline.NET"
  ```

  Nothing else about the package's telemetry changed. `ApiPipelineTelemetry` and `MeterName` stay
  public in the core package.

- **The eleven per-phase `UseApiPipeline*` extension methods are now `internal`** (they were public
  and `[Obsolete]`). An obsolete method that still compiles is one somebody still calls, and calling
  it defeats the four ordering invariants this package exists to enforce. Accessibility is the
  guarantee; the attribute was only advice. `UseApiPipeline(pipeline => ...)` is the whole public
  pipeline surface.

- **`CorrelationIdMiddleware`, `SecurityHeadersMiddleware`, `EdgeRateLimitingMiddleware`,
  `RequestSizeMiddleware`, `CorrelationIdPropagationHandler` and `ValidatedSnapshot<T>` are now
  `internal`.** They are only reachable through `UseApiPipeline` and the `Add*` methods, and
  `ValidatedSnapshot<T>` in particular is a 199-line workaround for a framework behaviour — making
  it public API committed thirty applications to a type that exists only until Microsoft fixes
  `IOptionsMonitor`. Use `CorrelationHeaders.CorrelationId` / `.TraceId` in place of
  `CorrelationIdMiddleware.HeaderName` / `.TraceHeaderName`.

#### Performance

- **`RequestSizeMiddleware` and `SecurityHeadersMiddleware` no longer allocate an async state
  machine per request: 112 B/op saved per frame, measured.** Neither does anything after
  `await _next(context)` — the security headers are written by an `OnStarting` callback — so both
  now return the next delegate's task directly.

  The existing benchmarks could not see this and still cannot: their terminal delegate returns
  `Task.CompletedTask`, and against a synchronously-completing continuation an `async Task` method
  never hoists its state machine to the heap. Three `_SuspendingTerminal` scenarios were added over
  a terminal delegate that yields — which is what every real endpoint does, and the case in which
  awaiting costs a boxed state machine per frame. Both middleware went from 1.25 KB / 1.45 KB to
  1.14 KB / 1.34 KB against a 1.09 KB baseline. `CorrelationIdMiddleware` and
  `EdgeRateLimitingMiddleware` still await, correctly: one has a log scope to dispose after the
  request and the other a lease. See `docs/performance.md`.

#### Documentation

- **Three verified-wrong claims in `README.md` corrected.** The correlation-id span attribute was
  documented as `correlation_id`; it has been `apipipeline.correlation_id` since the namespacing
  change, so any trace query built from the README returned nothing.
  `AddApiPipelineVersioning` was documented as throwing when called before `AddApiVersioning(...)`
  "since it decorates the reporter that call registers" — it decorates nothing and is
  order-independent, which is the whole point of the `Asp.Versioning` delegation. And the CORS
  opt-out was documented as preserving the application's own default policy, which it did not.

- Every reference to the removed satellite package and to the now-internal `Use*` methods updated
  across `README.md`, `CONTRIBUTING.md`, `docs/ARCHITECTURE.md`, `docs/INTERNALS.md`,
  `docs/OBSERVABILITY.md`, `docs/OPERATIONS.md`, `docs/RUNBOOK.md`, `docs/TROUBLESHOOTING.md`,
  `docs/migration.md`, the CORS / rate-limiting / response-caching / response-compression /
  forwarded-headers feature guides, and the sample's `README.md` and `Program.cs`.

- The exact-version pin on `FluentAssertions` now carries the reason it exists (8.x is commercially
  licensed), so it is not "tidied up" into a floating minimum.

#### Chore

- The stale `.worktrees/production-hardening` git worktree is removed. The
  `feature/production-hardening` branch it pointed at is untouched; the working copy was a full
  duplicate of `src/` and `tests/` on disk that doubled every repository-wide grep.

### First production readiness review follow-up (2026-09-02)

> Closes every finding of a full principal-architect / AppSec / SRE review — 3 P1, 6 P2, 3 P3, and
> every maintainability, performance and documentation item — plus two defects the review itself did
> not catch. Two of the three must-fix items were reachable by a one-character edit to a mounted
> ConfigMap on a pod that was passing every health check. Review and per-finding status:
> [docs/archive/production-readiness-review-2026-09-02.md](docs/archive/production-readiness-review-2026-09-02.md).

#### Fixed

- **An invalid configuration reload no longer takes every request to HTTP 500.** Every options type
  read on the request path — `SecurityHeadersSettings`, `CorrelationIdSettings`,
  `EdgeRateLimitingOptions`, `CorsSettings`, `RateLimitingOptions` — is now served from a
  `ValidatedSnapshot<T>` holding the last value that **validated**.

  Reading `IOptionsMonitor<T>.CurrentValue` per request was not safe: when a reload fails
  validation, `OptionsMonitor.InvokeChanged` evicts its cache entry and then throws out of `Get`, so
  the entry is never repopulated and every later read re-throws for the life of the process. With a
  ConfigMap mounted as a volume and `reloadOnChange` on — the `WebApplication.CreateBuilder` default
  — a one-character edit returned 500 to every caller while liveness probes that bypass the pipeline
  kept passing, so nothing restarted the pod. `ValidateOnStart` gave no protection: it had already
  run.

  An invalid reload is now a no-op plus a signal: the previous values stay in effect,
  `apipipeline.configuration.reload_rejected` increments, an `ILogger` error records the failure,
  and none of this package's own reload listeners throws. Binder failures (a value of `"abc"` for
  an `int`) are caught alongside validation failures. Snapshots are constructed at host start rather
  than on first use, so a reload cannot poison one before it exists, and nothing in the package
  resolves `IOptionsMonitor<T>` for these types any more.

  One background exception is out of reach and is the framework's: `ValidateOnStart()` is
  implemented as `Configure<IOptionsMonitor<TOptions>>(...)`, so it constructs a monitor whose own
  reload listener throws. That is unreachable from request handling — it is documented in
  [OBSERVABILITY.md](docs/OBSERVABILITY.md#hot-reload-last-known-good) rather than worked around,
  because the alternative is giving up fail-fast startup validation.

- **A live configuration change can no longer switch rate limiting off.** A policy name renamed or
  removed by a reload that itself validated left every endpoint carrying
  `RequireRateLimiting("<name>")` resolving to `RateLimitPartition.GetNoLimiter` — the security
  control silently and completely disabled, with nothing logged and nothing metered.

  The **last policy configured under that name** is now enforced instead, with
  `apipipeline.ratelimit.policy_unresolved` per request and an `ILogger` error once per
  configuration snapshot. Failing closed was considered and rejected: it would turn a one-word typo
  into a total outage, which is the same class of failure as the defect above.

- **One CIDR parser at the trust boundary.** `ForwardedHeadersSettings.KnownNetworks` is parsed with
  `System.Net.IPNetwork.TryParse`, replacing two independent hand-rolled implementations — one
  deciding what went into `KnownIPNetworks`, the other deciding whether the Production trust guard
  fired. A divergence between them meant the guard's verdict stopped matching the trust set it was
  guarding. The configured-entry count is now a by-product of the same parse, so the two cannot
  disagree. `IPNetwork.TryParse` also masks host bits (`10.1.2.3/8` → `10.0.0.0/8`) and validates
  the prefix length against the address family, both of which are strictly more correct than the
  code they replace. −55 lines.

#### Security

- **`ProcessHost` now requires a real `AllowedHosts`.** **Breaking (startup).**
  `ForwardedHeadersOptions:ProcessHost = true` fails Production startup when `AllowedHosts` is
  unset, empty, `"*"`, or a list containing `*`. Applying `X-Forwarded-Host` makes `Request.Host`
  caller-controlled unless something else constrains it — the common ingress controllers copy the
  caller's own `Host` into that header, so a trusted proxy does not make the value trustworthy — and
  `Host` feeds URL generation, redirect targets, password-reset links, and cache keys. Host
  filtering is what constrains it, is on by default in every `WebApplication`, and defaults to
  `AllowedHosts: "*"`, which allows everything. The settings had documented the pairing since 1.x;
  this makes it a guarantee. Outside Production it warns. `ProcessHost` still defaults to `false`,
  so this affects only deployments that turned it on.

- **`X-RateLimit-*` on a `429` is now the operator's choice.** New
  `RateLimitingOptions:EmitRateLimitHeadersOnRejection`, ~~defaulting to `true` — the 1.x
  behaviour~~. *Superseded within this release: the default is now `false` — see "Second production
  readiness review follow-up" above.*
  Set it to `false` on an internet-facing API: those headers hand an unauthenticated caller the
  exact permit limit and window to calibrate a flood against, and the edge limiter rejects before
  authentication, so there is no way to send them only to trusted callers. `Retry-After` and
  `Cache-Control: no-store` are always sent — a client needs `Retry-After` to behave, and it
  discloses the window rather than the quota. The setting governs both limiters.

- **API compatibility validation actually runs now.** It had been off twice over: the baseline was
  gated behind an unset repository variable, *and* `EnablePackageValidation` carried
  `Condition="'$(IsPackable)' == 'true'"` in `Directory.Build.props`, where the SDK has not yet
  defaulted `IsPackable` — so the condition was always false and no version of this package has ever
  been API-checked. Moved to a new `Directory.Build.targets`, where `IsPackable` is known and a
  project can override its own baseline; pinned to the last released version rather than a
  repository variable that can silently switch it off. The 44 deliberate v1→v2 breaks are recorded
  in `src/ApiPipeline.NET/CompatibilitySuppressions.xml`, so each one is reviewed in a diff and any
  break that is *not* listed fails `dotnet pack`.

#### Changed

- **A conflicting application CORS default policy now fails Production startup.**
  **Breaking (startup).** `LiveConfigCorsPolicyProvider` replaces the host-wide
  `ICorsPolicyProvider`, so an application's own `AddCors(o => o.AddDefaultPolicy(...))` stops being
  consulted. That was previously a startup **warning**, and a warning scrolls past in a pod log
  during a rollout — the symptom of missing it is browser requests failing in production with
  nothing in the application's own code to point at.

  With both an application default policy and `CorsOptions:Enabled=true`, Production startup now
  throws with the same remedy text. Two configurations claiming the default policy is an ambiguity,
  not a preference. Non-Production still warns, and `CorsOptions:Enabled=false` is not a conflict.
  To keep your own default policy, set `CorsOptions:Enabled` to `false`. *(Corrected within this
  release: this entry first advised `AddApiPipelineCors = false`, a flag that was removed with
  `ApiPipelineServiceRegistrationOptions`, and `Enabled=false` did not hand the default lookup back
  to the application until the second review's CORS fix above.)*

- **The pipeline order guard reports when it has become inert.** `PipelineOrderGuard` detects an
  explicit `UseRouting()` through ASP.NET Core's internal `"__EndpointRouteBuilder"` application-builder
  property. It now probes that key at host start and logs an **error** naming the runtime if the key
  is no longer set, instead of silently reporting no violation for a genuinely misordered pipeline.
  A framework-behaviour checklist for .NET major upgrades was added to `CONTRIBUTING.md`.

- **`IPv6PartitionPrefixLength` is bound, not smuggled.** The edge limiter read the value through a
  manual `int.TryParse` of one raw configuration key, working around the binder appending to
  `List<RateLimitPolicy>` on a second `Bind` of `RateLimitingOptions`. Both limiters now bind a new
  `RateLimitPartitioningOptions` from the same `RateLimitingOptions` section; it holds no
  collections, so repeated binding is idempotent. The configuration key is unchanged.

- ~~**Middleware constructors take `ValidatedSnapshot<T>` instead of `IOptionsMonitor<T>`.**
  **Breaking (source, for anyone constructing these types directly).** Affects
  `SecurityHeadersMiddleware`, `CorrelationIdMiddleware` and `EdgeRateLimitingMiddleware`.~~
  *Superseded within this release: all three middleware types and `ValidatedSnapshot<T>` are now
  `internal`, so nothing outside the package can construct them — see "Second production readiness
  review follow-up" above.*

- **One rejection shape for both rate limiters.** **Breaking (response headers).** The edge limiter
  does not run through `RateLimiterMiddleware`, so it never reached
  `RateLimiterOptions.OnRejected` and wrote its own, different `429`: no quota headers where the main
  limiter always sent them. Both now write through one `RateLimitRejection.Write`, so a caller
  cannot tell which limiter rejected it from the response shape and an operator does not have to
  know which one to look for the headers on.

- **`CorrelationIdMiddleware` and `RequestSizeMiddleware` are convention-based.**
  **Breaking (source, and for anyone resolving them from DI).** Neither implements `IMiddleware`,
  neither is registered in the container, and neither has `InvokeAsync(HttpContext,
  RequestDelegate)` — they have `Invoke(HttpContext)` and take `RequestDelegate next` in the
  constructor. `UseMiddleware<T>` instantiates a convention-based middleware once for the life of
  the application; the `IMiddleware` pattern cost an `IMiddlewareFactory.Create`, a transient
  container resolution, and a `Release` on every request, for types whose dependencies are all
  singletons. `AddApiPipelineCorrelationId(configuration)` still registers the options
  infrastructure the middleware reads, which was always the part that mattered. *(Both types are
  now also `internal` — see "Second production readiness review follow-up" above.)*

- **The correlation-id span attribute is `apipipeline.correlation_id`.** **Breaking (trace
  queries).** Was the bare `correlation_id`. There is no OpenTelemetry semantic convention for an
  application correlation id, so any name is a private one — and a private name in the global
  attribute namespace is the one that collides: two libraries writing `correlation_id` onto the same
  server span means the last writer wins silently, and the value an operator searches by may not be
  the one this package generated. `ApiPipelineTelemetry.CorrelationIdTagName` is the constant.

- **`ForwardedHeadersConfigurator` extracted.** `UseApiPipelineForwardedHeaders` was 40 lines of
  comment over code doing five things — parse, validate, count, guard the trust set, guard host
  processing — because each needed a paragraph about framework behaviour a reader cannot infer. Each
  rule is now its own named method on its own type, so those paragraphs are documentation for the
  thing they describe rather than inline archaeology, and each is testable on its own.
  `ApplicationBuilderExtensions.cs` drops from 592 to ~440 lines. No behaviour change beyond the
  `ProcessHost` guard above.

#### Deprecated

- ~~**The eleven individual `UseApiPipeline*()` methods are `[Obsolete]`** and will be removed in
  the next major version.~~ *Superseded within this release: they are `internal` in 2.0.0 rather
  than obsolete — see "Second production readiness review follow-up" above.* They carry none of the four ordering invariants the package exists to protect
  — security headers ahead of every short-circuiting phase, edge rate limiting before
  authentication, identity rate limiting after it, the caches after it — and a hand-wired pipeline
  can violate all four silently, with a response cached before authentication served to the wrong
  caller and nothing reporting an error. Use `UseApiPipeline(pipeline => ...)`. Shipping v2 with
  both public paths would have been a commitment to both forever.

#### Removed

- **Edge-limiter rejections moved to their own instrument.** **Breaking (dashboards and alerts).**
  `EdgeRateLimitingMiddleware` recorded `apipipeline.ratelimit.rejected` with
  `policy_name = "edge"`, sharing an instrument with the main limiter. It now records
  `apipipeline.edge_ratelimit.rejected`, untagged.

  The two paths are not the same kind of signal. The main limiter's rejections are already counted
  by the framework's `aspnetcore.rate_limiting.requests`, so that series is duplicative and is kept
  only for its `partition_type` dimension. The edge limiter runs before authentication as a plain
  `PartitionedRateLimiter` and never passes through `RateLimiterMiddleware`, so it emits **no**
  `aspnetcore.rate_limiting.*` measurement at all — its counter is the only signal that path has.
  One series that is duplicated for some of its values and load-bearing for others is the wrong
  thing to hand an operator during a flood.

  Update any query filtering `apipipeline.ratelimit.rejected` on `policy_name="edge"`.

- **`EdgeRateLimitingMiddleware.DisposeAsync` and its `IAsyncDisposable`.** Dead code:
  `app.UseMiddleware<T>()` activates convention-based middleware through
  `ActivatorUtilities.CreateInstance` and never disposes it, so the implementation could not run. It
  read as lifecycle management while doing nothing — worse than absent, because a reader would
  believe the limiter is released. The limiter is one instance holding the partition table the
  pipeline limits against, and correctly lives for the process.

- **`SecurityHeadersMiddleware`'s per-response `LogDebug`, and its `ILogger` dependency.**
  **Breaking (source, for anyone constructing the type directly).** It logged "Security headers
  applied to response" on **every** response, carrying nothing that identified the request, so it
  could not be used to investigate one — while costing a level check per response and a field in the
  boxed `OnStarting` state tuple, which was the only reason the middleware took an `ILogger` at all.
  The constructor no longer accepts one.

- **`apipipeline.deprecation.headers_added`, its telemetry decorator, and the startup guard for it.**
  **Breaking (dashboards and alerts).** Recording that one counter required decorating
  `Asp.Versioning`'s `IReportApiVersions` through ~40 lines of `ServiceDescriptor` removal and
  re-registration, a three-way descriptor materialiser, a private marker type for idempotency, a
  scoped resolution from a singleton `IHostedService`, and a documented public ordering constraint —
  278 lines in total, and the most fragile code in the repository.

  All of it is gone. `AddApiPipelineVersioning` now registers the bound options, one
  `IConfigureOptions<ApiVersioningOptions>`, and an options-only startup warning; it inspects no
  registered service. **Both ordering constraints are lifted:** it may be called before or after
  `AddApiVersioning(...)`, and before or after any custom `IReportApiVersions`. The
  `InvalidOperationException` it used to throw for the wrong order no longer exists.

  For the same signal, join `http.server.request.duration` against the versions you have configured
  as deprecated — the route dimension is the one the counter carried.

- **`ApiPipelineServiceRegistrationOptions` and the `AddApiPipeline` overload that took it.**
  **Breaking.** `AddApiPipeline(configuration)` registers every feature; each stays inert until its
  own `Enabled` flag is true in configuration. Two switches for one feature meant an operator could
  set `ResponseCachingOptions:Enabled=true`, watch nothing happen, and have no way to discover the
  reason was a line of C# in a file they were not looking at. `docs/migration.md` §19 maps every
  removed flag to its configuration equivalent; two need attention, because
  `AddApiPipelineRequestLimits=false` also suppressed the Kestrel hardening and
  `AddApiPipelineRequestSizeTracking` had no `Enabled` flag to map to.

- **The edge limiter's per-rejection `LogDebug`.** Invisible at production log levels, so during a
  flood it produced nothing at all — while the same line at `Information` would be a log flood
  driven by exactly the traffic being shed. `apipipeline.edge_ratelimit.rejected` is the signal.

- **`EdgeRateLimitingMiddleware`'s `ILogger` constructor parameter.** **Breaking (source, for anyone
  constructing the type directly.)** Nothing logs there any more.

#### Added

- **Rate-limiter and concurrency benchmarks, and committed allocation baselines.**
  `RateLimiterPipelineBenchmarks` drives a real `UseRateLimiter` pipeline over this package's own
  registration — lease acquisition, the partition factory, the resolver hop — and
  `ConcurrentPipelineBenchmarks` runs the full pipeline at 1, 8, and 64 concurrent requests through
  a *single* partition, which is where the only lock contention in the package lives. Neither
  existed: the costliest and most contended middleware had no benchmark, only partition-key
  microbenchmarks. `docs/performance.md` now commits **allocations per operation**, which are
  deterministic across machines and therefore reviewable, while still committing no timings.

#### Documented

- **Build vs. buy is now written down** in `docs/ARCHITECTURE.md`: why HSTS is a deliberate
  re-implementation of `HstsMiddleware` and what it adds, and why
  `NetEscapades.AspNetCore.SecurityHeaders`, `Microsoft.AspNetCore.HeaderPropagation` and `Scrutor`
  were each evaluated and declined.
- **`apipipeline.ratelimit.rejected` overlaps `aspnetcore.rate_limiting.requests`** — `OBSERVABILITY.md`
  now says so, explains that `partition_type` is the only non-duplicative dimension and why it is
  worth keeping, warns against summing the two counters, and notes that edge-limiter rejections
  produce no framework metric at all.
- **What a configuration reload does and does not change** for rate limiting —
  `docs/features/rate-limiting.md` now covers all four cases: limits inside an existing policy
  (live), a new policy name (needs a restart), a name renamed or removed (last known good, loudly),
  and an edit that fails validation (discarded).
- **The second, unvalidated configuration read in `RateLimiterRegistration`** is now explained at the
  call site: `AddPolicy` takes a policy *name*, so the set of names must be known while the service
  collection is still being built, before any `IServiceProvider` exists.

- **`Cache-Control` is deliberately not defaulted**, and `docs/features/security-headers.md` now
  says why — the correct value is per endpoint, a wrong global default is worse than none — with the
  per-endpoint and whole-app recipes, and a note that the pipeline's own caches are auth-safe
  regardless because this is about caches outside the process.
- **API keys behind `KnownApiKeyHashes` must be high-entropy random values.** The hash is unsalted
  and fast by design, because this is a partition identifier and the requirement is collision
  resistance — which makes the list reversible for any guessable key, and the list is not secret:
  it sits in a ConfigMap, in Git, and in every pod that mounts it. Nothing can enforce it, since a
  hash of a weak key is indistinguishable from a hash of a strong one.
- **The compression/caching order, and the contradiction fixed.** `docs/ARCHITECTURE.md` said the
  cache stores an *uncompressed* entry (correct — compression is phase 12, outside the caches);
  `docs/features/response-compression.md` said the opposite in two places and
  `docs/features/response-caching.md` in a third. All three now agree, and
  response-compression.md carries the actual trade-off table — one entry plus CPU per hit, versus
  one entry per encoding plus `Vary: Accept-Encoding` correctness — with when to reconsider and how
  to measure it.
- **Why `ResponseCompressionOptions.MimeTypes` stays a `string[]`** rather than a `FrozenSet`:
  the list is ten entries, `ResponseCompressionProvider` scans it linearly per response, and a
  linear scan of ten strings beats hashing the content type first.
- **The archived reviews moved to `docs/archive/`** with a README saying plainly that none of them
  describes the package as it stands. A review sitting alongside current documentation gets read as
  current documentation.

### Architecture review follow-up (2026-08-31)

> Addresses the findings of a full security/performance/maintainability review. Adds three further
> breaking changes to this release — a renamed interface, a changed default, and a renamed metric —
> each with its migration below.

#### Added

- **Kestrel slow-request and connection hardening.** `RequestLimitsOptions` gained
  `RequestHeadersTimeoutSeconds`, `KeepAliveTimeoutSeconds`, `MaxConcurrentConnections`,
  `MinRequestBodyDataRateBytesPerSecond` and `MinRequestBodyDataRateGracePeriodSeconds`, applied to
  the matching `KestrelServerOptions.Limits`.

  The package documented itself as providing "request hardening" while exposing only size limits.
  Kestrel allows **unlimited** concurrent connections by default, and a client that dribbles headers
  or a request body one byte at a time holds a connection with no middleware in this package running
  early enough to intervene. Setting the minimum rate to `0` disables minimum-rate enforcement for
  genuinely slow uploads; a grace period configured without a rate now fails at startup instead of
  enforcing nothing.

- **Request timeouts.** New `RequestTimeoutsOptions` section (`Enabled`, `DefaultTimeoutSeconds`,
  `TimeoutStatusCode`), `AddApiPipelineRequestTimeouts(configuration)`,
  `UseApiPipelineRequestTimeouts()` and a `WithRequestTimeouts()` pipeline phase, all delegating to
  ASP.NET Core's `RequestTimeouts` middleware. Ordered after the header phases so a 504 still carries
  the correlation id and security headers, and before authentication so the ceiling covers token
  validation. Per-endpoint overrides remain the framework's `WithRequestTimeout` /
  `DisableRequestTimeout` — this package ships no exclusion list of its own. See
  [docs/features/request-timeouts.md](docs/features/request-timeouts.md).

- **Pipeline order guard.** Host startup now fails when `app.UseRouting()` is called *after*
  `UseApiPipeline(...)`.

  That order moves routing behind the pipeline and suppresses the routing `WebApplication` would
  otherwise insert ahead of all middleware. CORS then stops honouring `[EnableCors("name")]`, rate
  limiting stops honouring `RequireRateLimiting("policy")` and silently applies the default policy,
  request timeouts stop honouring `DisableRequestTimeout`, and body-size telemetry loses its
  `http.route` dimension — with no error anywhere. The guard fires only when the pipeline actually
  contains an endpoint-aware phase, and the usual wiring (no explicit `UseRouting`, or `UseRouting`
  before the pipeline) is unaffected.

- **HSTS loopback exclusion.** `SecurityHeadersSettings.StrictTransportSecurityExcludeLoopbackHosts`
  (default `true`) and `StrictTransportSecurityExcludedHosts` (default empty). HSTS is no longer sent
  to `localhost`, `127.0.0.0/8` or `[::1]`, matching ASP.NET Core's own `UseHsts`. A browser that
  accepts HSTS for `localhost` afterwards refuses plain HTTP to every local development server.

  The loopback set is a flag rather than a defaulted list on purpose: the configuration binder
  **appends** to an existing collection, so a list shipped with defaults could only be added to,
  never cleared.

- **`apipipeline.correlation_id.rejected` counter.** A rejected inbound `X-Correlation-Id` was
  previously visible only at `Debug` level, so "a caller's ids are being discarded, and their logs
  cannot be joined to ours" was invisible in production.

- **Startup warning when compression is enabled but not for HTTPS.** `ResponseCompressionOptions`
  with `Enabled: true` and `EnableForHttps: false` is valid, starts cleanly, and compresses nothing
  in the deployment this package targets — behind a TLS-terminating ingress with forwarded headers
  applied, every request is HTTPS as far as the server is concerned.

#### Changed — breaking

- **`IApiVersionReader` renamed to `IApiPipelineVersionReader`.** **Breaking.**

  The old name collided with `Asp.Versioning.IApiVersionReader` — a different interface, with a
  different contract, resolvable in the same file through implicit usings. Any custom reader needs
  the new name:

  ```diff
  - public sealed class HeaderVersionReader : IApiVersionReader
  + public sealed class HeaderVersionReader : IApiPipelineVersionReader
  ```

  Consumers of `ApiPipeline.NET.Versioning` who call `AddApiPipelineVersioning(configuration)` need
  no change.

- **`RateLimitingOptions.EmitRateLimitHeaders` now defaults to `false`.** **Breaking (behavioural).**

  A `429` still carries `X-RateLimit-Limit` and `X-RateLimit-Window` — that is now written by the
  rejection callback, which was already setting headers, so it costs nothing and cannot be switched
  off. What became opt-in is putting those headers on **successful** responses, which required an
  extra middleware frame and a `Response.OnStarting` registration on every request in the process
  for information the client only acts on after being throttled.

  Set `"EmitRateLimitHeaders": true` to restore the previous behaviour.

- ~~**`apipipeline.deprecation.headers_added` retagged from `api_version` to `http.route`.**~~
  *Superseded within this release: the counter was removed entirely — see "Production readiness
  review follow-up" above.* The counter was recorded by a decorator around
  `Asp.Versioning`'s `IReportApiVersions`, not by this package's own middleware, and that decorator
  never consults a configured version list — re-deriving `api_version` would mean re-implementing the
  version matching this release deletes. `http.route`, bounded by the number of endpoints, answers
  the operational question just as well. Update any dashboard or alert built on the old dimension.

- **`ApiPipeline.NET.Versioning` now depends on `Asp.Versioning.Http` instead of
  `Asp.Versioning.Mvc`.** **Breaking (compile, MVC consumers).** The satellite no longer needs MVC's
  controller-routing types to supply deprecation policy values to `Asp.Versioning`. Applications
  routing through MVC controllers (`[ApiVersion]` / `[MapToApiVersion]`) add `Asp.Versioning.Mvc`
  themselves.

- **Deprecation headers no longer appear on short-circuited responses (401, 429, CORS preflight).**
  **Breaking (behavioural).** `Asp.Versioning` reports a version only on responses that reach the
  matched endpoint; a request rejected before then never gets there. The 1.x middleware wrote headers
  regardless of how the request ended. Measured during the migration and accepted rather than shimmed
  back in — see
  [docs/features/version-deprecation.md](docs/features/version-deprecation.md#behaviour-change-short-circuited-responses).

- **Deprecation dates and links now take effect on restart, not live.** **Breaking (behavioural).**
  1.x read `IOptionsMonitor.CurrentValue` per request, so editing `appsettings.json` changed the
  emitted headers immediately. `ConfigureDeprecationPolicies` now runs once, at startup, into the
  singleton `ApiVersioningOptions`, so a changed `DeprecationDate` or `SunsetDate` needs a process
  restart. Values still live in configuration rather than code — only the "takes effect immediately"
  part of that capability is gone.

- **Metric `apipipeline.request.body_bytes` renamed to `http.server.request.body.size`.**
  **Breaking (dashboards and alerts).**

  It is the OpenTelemetry semantic convention name for this measurement, so it lands with the rest of
  an operator's `http.server.*` metrics instead of in a package-specific corner. ASP.NET Core does
  not emit this instrument itself (it is opt-in in the specification), so nothing is duplicated.
  Update any dashboard or alert referencing the old name.

- **`ApiPipelineServiceRegistrationOptions.AddApiPipelineOutputCaching` removed.** **Breaking
  (compile).** It was already `[Obsolete]` and ignored; shipping a flag that does nothing is worse
  than removing it. Output caching services are always registered and stay inert until
  `OutputCachingOptions:Enabled` is `true`.

#### Fixed

- **`apipipeline.deprecation.headers_added` no longer tags a caller-controlled value.** The counter
  recorded the API version *as the caller spelled it*, while version matching is deliberately lenient
  — `1.0`, `v1`, `0001.0000` and unlimited further spellings all match a configured `1.0`, because
  leading zeros are unbounded. Each distinct spelling became a separate metric series: a permanent
  allocation in the SDK and a series in the backend, mintable at will by one client. The tag now
  always carries the **configured** version string. The XML documentation claiming the value space
  was "bounded by configuration" was wrong; it is now true.

#### Performance

- The `Strict-Transport-Security` value is built once per settings snapshot instead of being
  concatenated on every response.
- The rate limiter's partition factories no longer resolve `RateLimiterPolicyResolver` from
  `RequestServices` on every request; the singleton is resolved once and reused.
- `GC.KeepAlive` was removed from the response-compression path predicate; the reload registration is
  now rooted in a field of a small holder type, which is both cheaper and clearer.

#### Removed

- **`CorrelationIdMiddleware.CorrelationScope`.** Forty lines of custom `IReadOnlyList` struct plus a
  hand-written enumerator, whose stated purpose was avoiding a per-request array allocation. It did
  not achieve that: `ILogger.BeginScope` passes its state to
  `IExternalScopeProvider.Push(object?)`, which boxes the struct. Replaced with a two-element array.
- **The hand-rolled FNV-1a hash in `RateLimitPolicySignature`.** Replaced with `HashCode.Combine`.
  Partition keys never leave the process, so a per-process randomized hash is the right tool and the
  `StringBuilder` it was hashing over is gone. Reload behaviour is unchanged and still covered by
  `RateLimitingPartitioningTests`.

- **The custom API version deprecation subsystem.** **Breaking.** `WithVersionDeprecation()`,
  `UseApiVersionDeprecation()`, `AddApiPipelineVersionDeprecation()`, `IApiPipelineVersionReader`,
  `ApiPipelineConfigurationKeys.ApiVersionDeprecation`, and
  `ApiPipelineServiceRegistrationOptions.AddApiPipelineVersionDeprecation` are gone.
  `ApiVersionDeprecationOptions` moves to the `ApiPipeline.NET.Versioning` namespace and drops
  `PathPrefix` — it existed only because the old middleware ran without knowledge of which endpoint
  the request had matched; `Asp.Versioning`'s policies resolve against the endpoint's *declared*
  version directly, so there is nothing left to approximate with a path prefix. `Asp.Versioning` 10
  already emits `Deprecation`, `Sunset` and `Link` from its own policy managers, and does it more
  completely — it also emits a `rel="deprecation"` link and `api-deprecated-versions`. Core now
  carries no deprecation code at all. See [docs/migration.md](docs/migration.md) and
  [docs/features/version-deprecation.md](docs/features/version-deprecation.md).

#### Documentation

- Removed the README claim of "Structured RFC 9457 exception handling" — that code was deleted in
  2.0.0 and error handling is the application's, via `AddProblemDetails()` / `UseExceptionHandler()`.
- Removed README references to `options.AddApiPipelineOutputCaching = true`, a flag that was ignored
  and is now gone.
- Fixed two find-and-replace corruptions in the README that claimed
  `AddApiPipelineResponseCompression` "configures ASP.NET Core `AddApiPipelineResponseCompression`".
- Removed the README's `apipipeline.correlation_id.processed` metric, which never existed, corrected
  the deprecation counter's dimension name (`api_version`, not `apipipeline.api_version`), and
  corrected the claim that `AddApiPipelineTelemetrySources()` registers an `ActivitySource` — the
  package publishes none by design.
- `docs/performance.md` and `docs/OPERATIONS.md` named benchmark scenarios that no longer exist
  (`MinimalPipeline_GetPing`, `FullPipeline_GetPing`); they now list the real ones and state plainly
  that no baseline numbers are committed.
- `docs/features/cors.md` now states why the package replaces the host's `ICorsPolicyProvider`, what
  that costs, and how to opt out.

### Original 2.0.0 work

> Renamed extension methods, `IApplicationBuilder` receiver, removed telemetry members, removed
> `Skip*` builder methods, `IApiPipelineBuilder` replaced by a sealed class, two moved/renamed
> configuration keys, and a changed `Deprecation` header format. Under Semantic Versioning that is a
> major version, which is why the project version moved to `2.0.0`.

#### Removed

- **The request validation abstraction is gone.** `IRequestValidationFilter`,
  `RequestValidationResult`, `RequestValidationMiddleware`, `AddApiPipelineRequestValidation<T>()`,
  `UseApiPipelineRequestValidation()`, and the `WithRequestValidation()` pipeline phase have all
  been removed. **Breaking.**

  It duplicated `IEndpointFilter`, which ASP.NET Core provides and which Microsoft's own
  documentation names as the mechanism for validating request parameters and bodies. The framework
  version wins on every dimension that was measured: it reads the bound arguments (the middleware
  could not — no arguments, and the body is not seekable without `EnableBuffering`), it applies to
  the routes it is attached to instead of every request in the process including `/health` and
  unmatched 404s, it is visible to OpenAPI through `.ProducesProblem(400)`, and a single filter
  class serves both minimal APIs and MVC controllers.

  **Migration.** Implement `IEndpointFilter` and attach it where it applies:

  ```csharp
  public sealed class TenantHeaderFilter : IEndpointFilter
  {
      public async ValueTask<object?> InvokeAsync(
          EndpointFilterInvocationContext context, EndpointFilterDelegate next)
      {
          if (!context.HttpContext.Request.Headers.ContainsKey("X-Tenant-Id"))
          {
              return Results.Problem(
                  detail: "The X-Tenant-Id header is required on this endpoint.",
                  statusCode: StatusCodes.Status400BadRequest);
          }

          return await next(context);
      }
  }

  // Minimal APIs
  app.MapGroup("/api").AddEndpointFilter<TenantHeaderFilter>();

  // MVC controllers — note the two type arguments
  app.MapControllers()
     .AddEndpointFilter<ControllerActionEndpointConventionBuilder, TenantHeaderFilter>();
  ```

  `Results.Problem(...)` goes through `IProblemDetailsService`, so a rejection keeps the same
  `correlationId`, `traceId`, and `Cache-Control: no-store` the removed middleware produced.

  For model and property validation, use neither: `[ApiController]` returns an automatic
  `400 ValidationProblemDetails` for controllers, and `builder.Services.AddValidation()` does the
  same for minimal APIs.

  **One behaviour is genuinely lost.** The middleware ran at a pipeline phase *before* rate
  limiting, so a rejected request consumed no permit; endpoint filters run inside endpoint
  execution, after the limiter has granted one. An application that needs pre-limiter rejection
  should use `WithEdgeRateLimiting()` or a few lines of its own `app.Use(...)` middleware.

- **All 15 `Skip*` builder methods.** They only had an effect when paired with the matching `With*`
  call in the same chain, so `.WithCors().SkipCors()` was a longer way of writing nothing and a lone
  `.SkipCors()` did nothing at all — including in the README's own example. Delete the calls; omit
  the `With*` call to leave a middleware out.
- **Commented-out `IProblemDetailsService` blocks** in the rate limiter's `OnRejected` and in
  `RequestValidationMiddleware`, along with the unused constructor parameter they existed to justify.
  Both rejection paths already produce RFC 9457 bodies via `UseStatusCodePages`, which sits outside
  them in the phase order. Tests now assert that.
- **Committed BenchmarkDotNet result artifacts.** They were recorded with `Job=Dry`,
  `RunStrategy=ColdStart`, `IterationCount=1` and reported 1.86 **ms** for an empty pipeline —
  roughly a thousand times the real cost. The directory is now git-ignored.

#### Fixed

- **`apipipeline.cors.rejected` never moved for the case it exists to catch.** The counter was only
  incremented on the branch where *no* origins were configured at all. With a populated
  `AllowedOrigins` list — the normal production configuration — a disallowed origin was rejected
  silently and the metric read zero, so a broken allow-list was invisible until users reported it.
  The configured-origin matcher is now wrapped so a rejection is counted, delegating to the
  policy's own normalized-origin comparison so which origins match is unchanged.

- **Rate limit configuration changes had no effect, while the response header advertised them.**
  `RateLimitPartition.Get*` invokes its options factory once per partition key and caches the
  limiter permanently, so raising `PermitLimit` and reloading configuration left every existing
  partition enforcing the superseded limit — while `X-RateLimit-Limit` reported the new one to
  clients. An operator raising a limit during an incident saw the config apply and the behaviour not
  change. Partition keys now include a signature of the policy's limit-defining values, so a
  configuration change produces new partitions and the advertised limit is always the enforced one.
- **`ApiPipeline.NET.Versioning` never emitted deprecation headers.** `AspVersioningApiVersionReader`
  returned `ApiVersion.ToString()`, which renders a URL-segment request for `v1` as `"1"`. That does
  not string-match a configured `"1.0"`, so the middleware silently found no match. The reader now
  emits the canonical `major.minor` form, and version comparison additionally tolerates equivalent
  spellings (`1`, `1.0`, `v1`). The package previously had no test coverage at all; it now has
  end-to-end tests against the real `Asp.Versioning` stack.
- **Deprecation headers were lost on short-circuited responses.** `WithVersionDeprecation()` ran last
  in the phase order, so a caller on a deprecated version who was rate limited, rejected by
  authorization, or served from cache never saw the warning. It now sits alongside
  `WithSecurityHeaders()`, ahead of every short-circuiting phase.
- **`Deprecation` header format now conforms to RFC 9745.** The value is a Structured Fields Date
  (`Deprecation: @1751328000`); the previous RFC 1123 date and bare `true` come from the superseded
  `draft-dalal-deprecation-header`. `DeprecationDate` is consequently **required** for every listed
  version, because RFC 9745 has no dateless form. `Sunset` remains an HTTP-date per RFC 8594.
- **`SuppressServerHeader` silently stopped applying.** It lived on `ForwardedHeadersSettings` but was
  only read when the unrelated `AddApiPipelineRequestLimits` registration was enabled, so disabling
  request limits quietly restored the `Server: Kestrel` header. It has moved to
  `RequestLimitsOptions`, applies independently of that section's `Enabled` flag, and
  `ConfigureKestrelOptions` is now registered unconditionally.
- **Rate limit rejection telemetry mis-reported the partition type.** It re-derived the value from
  the request and could never report `apikey`. The partition factory now records what it actually
  did.

- **Named rate limit policies were inert.** The configured default policy was installed as
  `RateLimiterOptions.GlobalLimiter`, which ASP.NET Core applies *in addition to* an endpoint's own
  policy, making the effective limit `min(default, named)`. `RequireRateLimiting("generous")` with a
  higher limit silently did nothing. The default is now suppressed when the endpoint declares its
  own policy.
- **`AnonymousFallback = Reject` (the default) returned 500, not 429.** It built a fixed-window
  limiter with `PermitLimit = 0`, which throws `ArgumentException` from the limiter constructor. It
  now uses a limiter that denies by design.
- **`X-RateLimit-Limit` reported the wrong limit** on endpoints with a named policy.
- `UseApiPipelineResponseCompression` no longer recurses when `ExcludedPaths` is configured.
- `ApiVersionDeprecationMiddleware` no longer allocates an `OnStarting` closure per request when the
  feature is disabled.
- Options-monitor change registrations are held, so hot-reload no longer stops working
  non-deterministically when the registration is collected.

- CORS never exposed `X-Correlation-Id` on responses, so browser JavaScript could not read it. Listing it in `CorsOptions:AllowedHeaders` only permitted it on the *request*. Both tracing headers are now always exposed.
- A correlation ID header sent more than once is now rejected rather than adopted as the comma-joined concatenation of its values.

#### Performance

- **API-key rate limiting hashed every request twice.** The partition factory computed SHA-256 once
  to check the key against `KnownApiKeyHashes` and again inside `ForApiKey` to build the partition
  key. The hash is now computed once and reused via the new
  `RateLimitPartitionKeys.ForApiKeyHash(hash, scope)` overload.

#### Changed

- `ApiPipelineServiceRegistrationOptions.AddApiPipelineOutputCaching` is marked `[Obsolete]`. It has
  never had any effect — `AddApiPipeline` always registers Output Caching services so that
  `OutputCachingOptions:Enabled` cannot be set with nothing happening and no signal why — and it
  will be removed in the next major version. Control output caching from configuration.
- Documentation for `RateLimitingOptions.EmitRateLimitHeaders` said the middleware emits
  `X-RateLimit-Reset`; it emits `X-RateLimit-Window`, deliberately, because the package does not
  track per-partition window start times.
- The test suite runs with `DisableTestParallelization`. Telemetry assertions observe a static
  `Meter`, so a `MeterListener` saw increments emitted by whatever test was running concurrently —
  making counter assertions pass without the code under test recording anything.

- **BREAKING — extension methods renamed.** Every middleware and service extension is now prefixed
  `UseApiPipeline*` / `AddApiPipeline*`. The previous names (`UseCors`, `UseResponseCaching`,
  `UseResponseCompression`, `AddCors`, `AddResponseCaching`, …) collided with the ASP.NET Core
  methods of the same name and, being more specific overloads on `WebApplication`, silently won
  overload resolution — `app.UseCors()` invoked the pipeline's version, not the framework's.
- **BREAKING — extensions now target `IApplicationBuilder`** instead of the concrete
  `WebApplication`, so the package works with the generic host, `Startup`-style hosting, and
  `TestServer`. `WebApplicationExtensions` is renamed `ApplicationBuilderExtensions`.
- **BREAKING — `X-RateLimit-Reset` renamed to `X-RateLimit-Window`.** The emitted value was the
  configured window length, not the defined meaning of `Reset` ("seconds until the limit resets"),
  which this package does not track per partition.
- **BREAKING — `ApiPipelineTelemetry.ActivitySource` / `ActivitySourceName` removed.** The package
  starts no spans; ASP.NET Core's instrumentation already emits the server span. The correlation id
  is still stamped onto the ambient activity. `AddApiPipelineTelemetrySources()` now registers only
  the `Meter`.
- **BREAKING — `apipipeline.correlation_id.processed` counter removed** as a strictly worse
  duplicate of `http.server.request.duration`'s count.
- **BREAKING — telemetry instrument fields are private.** Record through the `Record*` methods.
- **BREAKING — `CorsPolicyNames` and `ResponseCachingSettings.PreferOutputCaching` removed.** Both
  were dead: nothing read them, and `PreferOutputCaching` changed no behaviour by its own admission.
- `apipipeline.request.body_bytes` is now tagged with `http.request.method` and `http.route` (the
  route template, never the raw path).
- `OutputCachingOptions` is bound unconditionally, so `Enabled: true` in configuration takes effect
  without a second code-only opt-in.
- `ApiPipeline.NET.OpenTelemetry` depends on the official `OpenTelemetry.Extensions.Hosting`
  instead of the internal `OpenTelemetry.NET` package.
- `CodeStyle.NET` is `PrivateAssets="all"` in all three packages. It was published as a public
  dependency, forcing every consumer to authenticate to the private GitHub Packages feed just to
  restore. **The core `ApiPipeline.NET` package now has zero NuGet dependencies.**
- `ApiPipeline.NET` targets `Microsoft.NET.Sdk` (was `Microsoft.NET.Sdk.Web`) and declares a
  `PackageReadmeFile`; the package previously shipped without a readme.
- Benchmarks measure the built `RequestDelegate` against a `DefaultHttpContext` instead of driving
  `TestServer` through `HttpClient`, whose overhead dominated both timings and allocations.

- **BREAKING**: `HttpContext.Items` key for the correlation ID changed from `"X-Correlation-Id"` to `"CorrelationId"`. Use `context.GetCorrelationId()` instead of indexing `Items` directly.
- **BREAKING**: a generated correlation ID is now a fresh GUID rather than a copy of `Activity.TraceId`. The two IDs are reported separately; code that read the correlation ID expecting a trace ID should read `X-Trace-Id` / `GetTraceId()`.
- **BREAKING**: Configuration section `SecurityHeaders` renamed to `SecurityHeadersOptions` for naming consistency.
- CORS `AllowedHeaders` default changed from `["*"]` to `["Content-Type", "Authorization", "X-Correlation-Id"]`.
- Rate limiter uses `IOptionsMonitor` (singleton) instead of `IOptionsSnapshot` (per-request) in hot path.
- `CorrelationIdMiddleware` uses zero-alloc struct scope instead of per-request `KeyValuePair[]` allocation.
- Removed no-op `[MinLength(0)]` from `CorsSettings` array properties.

- **`IApiPipelineBuilder` is now the sealed class `ApiPipelineBuilder`.** Nothing outside the package
  has a reason to implement it, and a sealed type lets new phases be added without every addition
  being a public API surface decision. `UseApiPipeline(Action<ApiPipelineBuilder>)` — lambda call
  sites are unchanged.

#### Security

- **Rate limiting could be bypassed completely by rotating the API key header.** With
  `EnableApiKeyPartitioning: true` the raw, unauthenticated `X-Api-Key` value became the partition
  key, so a caller sending a different value on every request landed in a fresh bucket every time
  and was never limited — while growing the limiter's partition table without bound. Reproduced at
  10/10 permitted against a `PermitLimit` of 2. Because an authenticated caller is partitioned by
  `sub` first, this branch was only ever reachable by *unauthenticated* callers. The feature now
  requires an allow-list: `KnownApiKeyHashes` must list the SHA-256 hash (first 16 bytes, lowercase
  hex) of every key permitted its own partition, startup fails when it is empty, and any key not on
  the list falls through to IP partitioning. Documentation recommending the unsafe configuration
  ("per-client SLAs without requiring full authentication infrastructure") has been corrected.
- **IPv6 clients could bypass IP rate limiting.** Partition keys used the full `/128` address, so a
  client with a routed `/64` — the normal end-site assignment per RFC 6177 — had 2^64 buckets
  available. Addresses are now masked to `IPv6PartitionPrefixLength` (default `64`) before keying,
  and IPv4-mapped IPv6 addresses are normalised so a dual-stack listener no longer gives one client
  two buckets.
- **`X-Forwarded-Host` is no longer processed by default.**
  `ForwardedHeadersOptions:ProcessHost` now defaults to `false`. Trusting the connecting proxy is
  not the same as trusting the header: the common ingress controllers copy the caller's own `Host`
  into it (nginx-ingress sends `X-Forwarded-Host: $best_http_host`), so applying it made
  `Request.Host` caller-controlled even behind a correctly configured trust set — and `Host` feeds
  URL generation, redirect targets, absolute links in outbound mail, and cache keys. Reproduced:
  a request through a trusted `10.0.0.0/8` peer carrying `X-Forwarded-Host: attacker.example.com`
  set `Request.Host` to that value. `X-Forwarded-For` and `X-Forwarded-Proto` are unaffected. Set
  `ProcessHost: true` to restore the old behaviour, and only where the proxy sets the header itself
  and strips any inbound value. Pair either choice with `AllowedHosts`.
- **Security headers are on by default.** `SecurityHeadersOptions:Enabled` now defaults to `true`.
  The middleware only runs when the pipeline asked for it, so an application that called
  `WithSecurityHeaders()` and then silently received no headers because a configuration section was
  absent was a trap rather than a safe default. Every individual header remains independently
  switchable, and HSTS still self-suppresses in Development and over non-HTTPS transports.
- **Pre-authentication load shedding.** The main limiter runs after authentication so it can
  partition per user, which means an unauthenticated flood pays for signature validation on every
  request before being rejected. The new opt-in `WithEdgeRateLimiting()` phase sheds by client IP
  before authentication (and after CORS, so preflight is answered rather than shed).

- **Forwarded headers: fixed a trust-boundary hole.** `ClearDefaultProxies: true` with no
  `KnownProxies`/`KnownNetworks` left `ForwardedHeadersMiddleware` with an empty trust set, which
  makes it skip trust checking entirely and accept `X-Forwarded-For`, `X-Forwarded-Proto`, and
  `X-Forwarded-Host` from any caller — client IP, scheme, and host all became attacker-controlled,
  and IP-partitioned rate limiting was bypassable by rotating one header. The production guard was
  gated on `ClearDefaultProxies == false` and so could never fire on this path. It is now evaluated
  against the **effective** trust set, after parsing, and entries that fail to parse no longer count
  towards it. Documentation that recommended the unsafe setting has been corrected.
- **Security headers now cover short-circuited responses.** `SecurityHeaders` moved ahead of CORS,
  authentication, authorization, request validation, and rate limiting in the enforced phase order.
  It registers its `OnStarting` callback only when the request reaches it, so 401, 403, 429, and
  CORS preflight responses previously carried no HSTS and no `nosniff`.
- **CORS: wildcard origin with credentials is rejected at startup.** `AllowedOrigins: ["*"]` with
  `AllowCredentials: true` passed validation and then threw from `CorsPolicyBuilder.Build()` on the
  first CORS request of an already-Ready pod. The policy is now built during startup validation, so
  any configuration that cannot produce a policy fails fast.
- **CORS: a failed configuration reload no longer takes down a running pod.** The reload callback
  keeps the last known-good policy and logs an error.
- **API keys are no longer used verbatim as rate limiter partition keys.** They are hashed, keeping
  the plaintext credential out of a long-lived dictionary.

#### Added

- `WithEdgeRateLimiting()` / `EdgeRateLimitingOptions` — opt-in pre-authentication, IP-partitioned
  load shedding. Off by default; defaults are deliberately generous (600 requests / 60s) because
  before authentication a NAT'd office and an attacker look identical. Process-local like every
  other limiter here.
- `RateLimitingOptions.KnownApiKeyHashes` — allow-list of API key hashes permitted their own
  rate-limit partition. Required whenever `EnableApiKeyPartitioning` is true.
- `RateLimitingOptions.IPv6PartitionPrefixLength` (default `64`) — the IPv6 prefix that defines one
  rate-limited client.
- `ForwardedHeadersSettings.ProcessHost` (default `false`) — controls whether `X-Forwarded-Host` is
  applied.
- `RequestLimitsOptions.SuppressServerHeader` — moved here from `ForwardedHeadersSettings`.
- A startup warning when the pipeline's CORS policy provider overrides an application's own
  `AddCors(o => o.AddDefaultPolicy(...))`, which it silently replaced before.

- `SecurityHeadersSettings.StrictTransportSecurityRequireHttps` (default `true`).
- Startup validation rejecting `ResponseCachingOptions:Enabled` and `OutputCachingOptions:Enabled`
  together.
- CI (`build/test/pack`, vulnerable-package gate, CodeQL), Dependabot, and a tag-triggered release
  workflow that publishes from a protected environment using a short-lived token instead of a
  long-lived PAT.
- `NuGetAudit` at `low` level across all projects, and `EnablePackageValidation`.

- Correlation and trace headers now reach error responses from any exception handler: they are written from `Response.OnStarting`, so a handler calling `Response.Clear()` cannot wipe them.
- `X-Trace-Id` response header carrying the W3C trace ID of the request `Activity`, alongside `X-Correlation-Id`. A caller that supplies its own correlation ID can now still locate the trace.
- `CorrelationHeaders` — header names and `HttpContext.Items` keys for both tracing IDs.
- `CorrelationIdSettings` (`CorrelationIdOptions` section): `AcceptInboundCorrelationId`, `EmitTraceIdHeader`, `MaxLength`.
- `AddApiPipelineCorrelationId(IConfiguration)` overload that binds `CorrelationIdOptions`.
- `HttpContext.GetCorrelationId()` / `GetTraceId()` accessors.
- `CorrelationIdPropagationHandler` and `IHttpClientBuilder.AddApiPipelineCorrelationIdPropagation()` — forwards `X-Correlation-Id` onto outbound calls without overwriting an explicitly set value.
- Both tracing headers are added to `Access-Control-Expose-Headers` on every CORS policy.
- `TraceId` added to the correlation logging scope, alongside `CorrelationId`.
- `StrictTransportSecurityPreload` option for HSTS preload directive.
- `AddXFrameOptions` / `XFrameOptionsValue` for clickjacking protection.
- `ContentSecurityPolicy` and `PermissionsPolicy` optional security headers.
- `AnonymousFallback` option on `RateLimitingOptions` (default: `Reject`).
- `EnforceTrustedProxyConfigurationInProduction` on `ForwardedHeadersSettings`.
- `PreferOutputCaching` migration hint on `ResponseCachingSettings`.
- `ApiPipelineServiceRegistrationOptions` for selective feature registration.
- `WithRequestSizeTracking()` on pipeline builder (no longer requires standalone `UseApiPipelineRequestSizeTracking()` call).
- Phase-enforced middleware ordering in `ApiPipelineBuilder`.
- Rate-limit `OnRejected` telemetry now includes `policy_name` and `partition_type` dimensions.
- `FrozenDictionary` for immutable rate-limit policy snapshot.

#### Build and supply chain

- All GitHub Actions are pinned to full commit SHAs rather than mutable major-version tags.
- Release builds publish signed build provenance (`actions/attest-build-provenance`) alongside the
  existing SBOM. Verify with `gh attestation verify <file> --repo baps-apps/apipipeline-net`.
- CI runs public API compatibility validation against the previous release when the
  `API_COMPAT_BASELINE_VERSION` repository variable is set.

---

## CHANGELOG 2.0.0 section as of 2026-11-12, before it was rewritten against 1.1.0

Kept for history: many entries below describe changes between unreleased development builds (for
example fixes to the Redis package, which never shipped before 2.0.0), not changes from 1.1.0.

### [2.0.0] - Unreleased (development text)

A breaking release. Step-by-step upgrade instructions are in [docs/migration.md](docs/migration.md).
Development notes are archived in
[docs/archive/changelog-2.0.0-development-notes.md](docs/archive/changelog-2.0.0-development-notes.md).

### Added

- **RFC 9457 bodies on the package's own error responses.** A 429 from either rate limiter and a
  request timeout now have an `application/problem+json` body, written through the application's
  `IProblemDetailsService` when it called `AddProblemDetails()` (so `CustomizeProblemDetails`
  applies). Because the body starts the response, middleware that adds a header to a 429 after
  `next()` must use `Response.OnStarting`.
- **`ApiPipeline:Cors:PreflightMaxAge`** sets `Access-Control-Max-Age` (whole seconds, up to one
  day). Unset, nothing is sent, as before.
- **The CORS policy exposes `Retry-After`, `RateLimit` and `RateLimit-Policy`**, so a browser client
  can read when to retry a 429.
- **Request limits under IIS and HTTP.sys.** `MaxRequestBodySize` is applied to IIS in-process, and
  the body limit, connections and both timeouts to HTTP.sys. Settings a server cannot take from the
  application are named in a startup Warning (event 70) instead of being silently ignored.
- **Redis: `PartitionKeySecret`** (required outside Development, at least 32 characters). Keys are an
  HMAC-SHA256 of the partition key under it, so they cannot be matched to an IPv4 address or a
  guessed user id by anyone who can list them.
- **Redis: `ConcurrencyLeaseMaxDuration`** (default 1 hour). A concurrency lease held this long gives
  its slot back, so a lease that is never disposed holds a slot on every pod for at most this long,
  not until the garbage collector happens to collect it.
- **`SECURITY.md`**: how to report a vulnerability, and which versions get fixes.
- **Licence: proprietary, internal use only.** The packages were MIT-licensed, which let anyone who
  obtained them reuse them. `LICENSE` now reserves all rights to baps-apps and permits use only within
  the organization; each package ships the file (`PackageLicenseFile`) instead of an SPDX expression.
- **Health-probe exemption is narrower.** `ExcludedPaths` (main and edge limiters) exempt a request
  only when it reached the pod directly, as a kubelet probe does: a probe path called through a
  proxy (`X-Forwarded-For`, `Forwarded` or `X-Original-For`) is limited like any path, and an
  endpoint under a probe path that asks for a policy (`RequireRateLimiting`, `[EnableRateLimiting]`)
  gets it.
- **Redis: `Reject` policies are not gated by the circuit.** They ask Redis on every request and
  reject only when their own call fails, so another policy's failures or a CPU-starved pod's
  timeouts no longer turn into a 429 for every caller for a whole cooldown.
- **The default CORS provider is wrapped once.** With ASP.NET Core's `DefaultCorsPolicyProvider`, the
  package's wrapper is a singleton instead of being rebuilt (through reflection) on every request.
- **Error handling goes inside the pipeline** in the documented setup
  (`WithMiddleware(a => a.UseExceptionHandler())` right after `WithCorrelationId()`), so the
  unhandled-exception log line carries the `CorrelationId`. See the README.
- **`MaxIpPartitions`** on `ApiPipeline:RateLimiting` and `ApiPipeline:EdgeRateLimiting` (default
  `100000`) bounds the client-address partitions each limiter keeps in memory (OWASP API4). Past
  it, requests from new addresses share one overflow bucket per policy with the same limits until
  idle partitions expire. New metrics `apipipeline.rate_limiting.partitions.overflow` and
  `apipipeline.edge_rate_limiting.partitions.overflow`, and warning event 24.
- **Redis: a one-time server check.** After Redis first answers, each pod warns once if a server is
  older than 5.0 (event 104) or can evict rate limit keys under memory pressure, which resets
  limits silently (event 105).
- **Redis: `apipipeline.rate_limiting.store.duration`**, a histogram (seconds) of each rate-limited
  request's Redis call, per policy, with `error.type` when Redis did not answer. ASP.NET Core's
  `aspnetcore.rate_limiting.queued_requests` and `request.time_in_queue` count every Redis-limited
  request as queued; [OBSERVABILITY.md](docs/OBSERVABILITY.md#dashboards-and-alerts-with-the-redis-package)
  explains what to chart and alert on instead.
- **Redis: a `Concurrency` key expires with its last held slot.** Each acquire set the key's TTL to
  the caller's lease timeout, so during a rollout a pod with a shorter `ConcurrencyLeaseTimeout`
  could have the key (and another pod's longer slots with it) deleted early, admitting more than the
  limit. The key now expires at its latest member's expiry (`PEXPIREAT`, so Redis 5.0 still works).
- **Redis: the fallback counter is tagged with `apipipeline.rate_limiting.store.fallback_reason`**
  (`timeout`, `error`, `circuit_open`, `connecting`), and the `degraded` gauge has unit `{state}` (not `1`, which the OpenTelemetry Prometheus
  mapping would export as `..._degraded_ratio`, matching none of the documented alerts).
- **Redis: no connection is opened while `ApiPipeline:RateLimiting:Enabled` is false.** The startup
  checks still run.
- **Redis tests** cover key expiry for every kind, atomicity under 400 concurrent acquires from four
  connections, Redis 5.0 and a Redis Cluster node, a release sent during shutdown, and the
  token-bucket, sliding-window and crashed-lease tests no longer depend on real-time 1 s windows.
- **The sample's appsettings files** drop the unused `AppSettings`, `OpenTelemetryOptions` and
  `Serilog` sections, and compress HTTPS responses only in Development.

- **The Redis documentation states the real Redis cost of `GlobalPolicy`.** It said one Redis call
  per request; with `GlobalPolicy` set, a request makes two, one after the other, so Redis adds up
  to twice `CommandTimeout` (500 ms by default) to it. Now documented, and pinned by
  tests that count the calls in Redis.
- **The Redis package no longer silently replaces an application's own rate limiter factory.** An
  `IApiPipelineRateLimiterFactory` registered before `AddApiPipelineRedisRateLimiting` makes that
  call throw; one registered after it fails startup (whether or not `Redis:Enabled` is true). Both
  messages name the factory. Previously the first was replaced and the second silently replaced the
  Redis factory, so one of the two never ran.
- **A Redis concurrency lease that is never disposed no longer holds its slot for the life of the
  pod.** Its renewal timer kept the lease alive and renewed it forever, holding one slot on every
  pod; it now refers to the lease weakly, so once the lease is garbage-collected its slot is
  released at the next renewal. A lease still referenced is renewed as before, however long the
  request runs.
- **Redis sliding windows keep their configured length.** With Redis enabled, a `SlidingWindow`
  policy whose window in milliseconds does not divide evenly into `SegmentsPerWindow` fails startup,
  as does one with more than 100 segments. The script counts whole-millisecond segments, so an uneven
  window shrank silently (1,000 ms in 600 segments became 600 ms, admitting 1.67 times the limit),
  and it reads every segment on every request. This replaces the "segments of at least 1 ms" check,
  which it covers.
- **Redis rate limiting reports code that acquires only synchronously.** A Redis limiter answers
  `AttemptAcquire` with "not acquired" (it cannot reach Redis without blocking, and the middleware
  takes an acquired lease from that call as final), so code that calls only `AttemptAcquire` (a
  SignalR hub filter, a custom middleware) silently rejected every request. One Warning (event 103)
  now names the cause and the fix (`AcquireAsync`) once such attempts outnumber asynchronous
  acquires by 100. ASP.NET Core's middleware never triggers it.
- **Redis rate limiting survives a brownout.** The circuit also opens on a failure rate:
  `FailureRatio` (default 0.5) of the calls over `SamplingDuration` (10 seconds), once
  `MinimumThroughput` (20) calls were made. Before, only `FailureThreshold` failures in a row opened
  it, which a brownout (failures interleaved with successes) never reaches, so every request kept
  paying up to the command timeout. A broken key's `WRONGTYPE` reply counts as Redis answering, so a
  hot broken key cannot open the circuit for everyone; other error replies (`LOADING`, `BUSY`,
  `OOM`, ...) count as failures. And the connection the package opens from `Configuration` now
  caps StackExchange.Redis's `asyncTimeout` at `CommandTimeout` (a shorter value in the
  connection string is kept), so commands the limiter stopped waiting for are dropped at the
  client's next heartbeat instead of piling up for the 5 s default.
- **Redis rate limiting: choose what a policy does while Redis is down, and keep a flood off
  Redis.** `ApiPipeline:RateLimiting:Redis:Fallback` (`PerPod`, the default, `Allow` or `Reject`)
  and `PolicyFallbacks` (by policy name, e.g. `{ "login": "Reject" }`) let abuse-protection
  policies fail closed instead of allowing N times the limit with N replicas; a name that matches
  no policy fails startup. Separately, once Redis rejects a partition until a known time, the pod
  rejects that partition locally until then (windows and buckets, not concurrency), so a flood
  against one caller costs Redis one call per pod per retry-after instead of one per request and
  cannot by itself slow Redis into opening the circuit for everyone.
- **Health probes are never rate limited by default.** `ApiPipeline:RateLimiting:ExcludedPaths`
  (default `["/health", "/internal/health"]`, matched by path segment) exempts probe paths from
  every policy, including `GlobalPolicy`. The default policy used to cover them, so kubelet probes
  (anonymous, from the node IP, and with the Redis add-on sharing one bucket across the node) could
  get 429 and have healthy pods restarted. The edge limiter's default `ExcludedPaths` gains
  `/internal/health` too, covering `/internal/health/liveness`, `/readiness` and `/startup`. A
  configured list replaces the default; entries must start with `/`.
- **Startup warning for enabled features that add no middleware.** A feature whose `Enabled` is
  `true` but whose `With*()` phase no `UseApiPipeline` call added (for example rate limiting
  without `WithRateLimiting()`) was silently inert; it is now reported once the app has started
  (event 13), as is `UseApiPipeline` never being called at all (event 14). Only features the
  application registered are checked, and phases from every `UseApiPipeline` call count. A
  warning, not a failure: middleware added outside `UseApiPipeline` is not visible to the check.
- **`ApiPipeline.NET.RateLimiting.Redis`**, an opt-in package that shares rate limit policies across
  replicas through Redis (`ApiPipeline:RateLimiting:Redis`). It covers all four kinds, falls back to
  per-pod limits while Redis is unavailable (a failed call never fails the request, whatever the
  exception; the process-wide circuit opens only after `FailureThreshold` failures in a row, default
  5), and adds the metrics
  `apipipeline.rate_limiting.store.fallback` and `apipipeline.rate_limiting.store.degraded`. See
  [rate-limiting.md](docs/features/rate-limiting.md#shared-limits-across-pods). Its limiters honour
  the `RateLimiter` permit-count contract (`0` probes without spending, `n` spends `n`), renew held
  concurrency leases (so `ConcurrencyLeaseTimeout` defaults to 30 seconds and only bounds how long a
  dead pod's slot stays taken), and require `KeyPrefix` outside Development (no `{` or `}`). Run it
  on a Redis with `maxmemory-policy noeviction`.
- **`IApiPipelineRateLimiterFactory`** and `InMemoryRateLimiterFactory` (namespace
  `ApiPipeline.NET.RateLimiting`): the public seam that creates the limiter behind each partition.
- **`ApiPipeline:RateLimiting:GlobalPolicy`**: a policy applied to every endpoint on top of its own,
  with ASP.NET Core's `GlobalLimiter` semantics (an overall per-caller ceiling). `DefaultPolicy` is
  unchanged and now documented as what it is: a fallback for endpoints without a policy, which does
  not cap named policies. Set `GlobalPolicy` to the same name as `DefaultPolicy` for the framework's
  own behavior. See [rate-limiting.md](docs/features/rate-limiting.md#default-named-and-global-policies).
- **Custom rate-limit partitioning.** `ApiPipeline:RateLimiting:PartitionClaims` (for example
  `["tenant_id"]` or `["azp", "client_id"]`) gives every caller sharing that claim one bucket, from
  configuration alone. For other logic, implement `IRateLimitPartitionKeyResolver` and register it
  with `AddApiPipelineRateLimitPartitionKeyResolver<T>()`. Both run ahead of the user → API key →
  IP chain; the package namespaces and hashes the key, and the partition type you choose is
  reported on `apipipeline.rate_limiting.rejected`. See
  [rate-limiting.md](docs/features/rate-limiting.md#custom-partitioning-tenant-or-client).
- **Startup warning for rate limiting on a Unix socket or named pipe.** Such listeners give every
  request a null client IP, so with `AnonymousFallback: Reject` every anonymous request (health
  probes included) gets 429. The warning names the listen address and the two fixes. Documented in
  docs/features/rate-limiting.md.

- **`DeprecationLink`** on a deprecated version: the `Link` header with `rel="deprecation"` (RFC
  9745), separate from `SunsetLink` (`rel="sunset"`).
- **Packages carry an icon, copyright and release notes**, and the core package has its own short
  README with absolute links (the 24 KB repository README, whose relative `docs/` links were dead on
  the package page, is no longer packed).
- **`WithMiddleware(Action<IApplicationBuilder>)`** puts the application's own middleware at any
  position in the pipeline (for example tenant resolution between authentication and rate
  limiting), and **`WithRouting()`** places routing explicitly.
- **`Action<TOptions>` overloads** for every `AddApiPipeline*` method and `AddApiPipelineVersionDeprecation`.
- **Edge rate limiting**, before authentication: `WithEdgeRateLimiting()` + `ApiPipeline:EdgeRateLimiting`.
- **Request timeouts**: `WithRequestTimeouts()` + `ApiPipeline:RequestTimeouts`.
- **Kestrel slow-client limits** in `ApiPipeline:RequestLimits`: `RequestHeadersTimeout`,
  `KeepAliveTimeout`, `MaxConcurrentConnections`, `MinRequestBodyDataRate*`.
- **Correlation options** (`ApiPipeline:CorrelationId`) and the `X-Trace-Id` header.
- **HSTS host exclusions** (`StrictTransportSecurityExcludeLoopbackHosts`,
  `StrictTransportSecurityExcludedHosts`), plus `ApiPipeline:ForwardedHeaders:ProcessHost`. With
  `ProcessHost`, an `X-Forwarded-Host` is applied only when it matches `AllowedHosts`.
- **New metrics:** `apipipeline.edge_rate_limiting.rejected`, `apipipeline.correlation_id.rejected`.
- **Trimming and Native AOT compatibility** through source-generated options validation and
  configuration binding.
- **Pipeline logging.** The composed pipeline is logged at startup, in execution order.
- **A README for the `ApiPipeline.NET.Versioning` package.**

### Changed

- **The edge limiter is disposed with the host.** Its partitioned limiter (and replenishment timer)
  now belongs to the host's container; before, each host leaked one timer.
- **Logging is source-generated** (`[LoggerMessage]`) in all three packages, with stable event IDs
  (core 1-99, Redis 100-199, Versioning 200-299; listed in
  [OBSERVABILITY.md](docs/OBSERVABILITY.md#logging)). Message text is unchanged. The per-request
  correlation scope now allocates one object instead of two, and prints as
  `CorrelationId:... TraceId:...` in the console logger. CA1848 is enforced for the shipped code.
- **A user id is escaped in rate limit partition keys** like a claim value: one containing `|`
  or a line break, or longer than 128 characters, is hashed. Only such ids get a new bucket.
- **`Vary: Origin` is added when CORS and response caching are both on, unless `Vary` already
  lists `Origin` or `*`.** The check compared substrings, so a `Vary: X-Forwarded-Origin` set by an
  endpoint suppressed it (a response cached for one origin could be served to another), and `*`
  got `Origin` appended needlessly.
- **`RateLimitPartitionKey` is a sealed record class**, not a record struct, so every instance has
  a valid `Type`: a struct's `default` had none. Resolvers return `null` for "not mine", as before.
- **The shipped packages build at `AnalysisLevel` `latest-recommended`**, with warnings as errors.
- **The policy signature in rate limit partition keys is deterministic across processes**
  (SHA-256-based), so replicas that share a configuration produce identical keys. Buckets start
  fresh once on upgrade.
- **A disabled feature leaves the framework's own settings alone.** The package sets the framework
  options for rate limiting (`GlobalLimiter`, `OnRejected`), response compression (providers, MIME
  types, levels), response caching and HSTS only while the feature is enabled, so an application's
  own `AddRateLimiter(...)`, `AddResponseCompression(...)`, `AddResponseCaching(...)` or
  `AddHsts(...)` keeps its values. Only Kestrel's `Server` header is still removed regardless (opt
  out with `ApiPipeline:RequestLimits:SuppressServerHeader: false`).
- **An application's own `ICorsPolicyProvider` is wrapped, not discarded.** Registered before
  `AddApiPipeline`, it keeps its lifetime, answers named lookups, and answers every lookup while
  `ApiPipeline:Cors:Enabled` is false. Registered after it with CORS enabled, it would silently
  bypass `ApiPipeline:Cors`, so startup now fails outside Development (a Warning in Development).

### Breaking: code

- **`RateLimitPolicy.AutoReplenishment` is removed.** It had no effect: each partition's limiter is
  replenished by the framework's partitioned limiter whatever its value, and the Redis limiters
  compute time on the server. Delete the assignment; a leftover configuration key is ignored. It
  was part of the policy signature, so buckets start fresh once on upgrade (as they do anyway with
  2.0.0's deterministic signatures).
- **Option collections are `IList<T>`** on every options type (they were `string[]`, and
  `List<RateLimitPolicy>` for `Policies`), following the Framework Design Guidelines (CA1002, CA1819)
  and ASP.NET Core's own options. Binding is unchanged; `.Length` becomes `.Count`.
- **`AddApiPipelineVersioning` is renamed `AddApiPipelineVersionDeprecation`** (class
  `ApiPipelineVersionDeprecationServiceCollectionExtensions`): it adds deprecation and sunset
  policies only.
- **Binding a feature to a second, different section throws** `InvalidOperationException` at
  registration, naming both sections. It was silently ignored. Repeating the same section is still
  a no-op.
- **Registration methods renamed.** `AddCors` became `AddApiPipelineCors`, `AddRateLimiting` became
  `AddApiPipelineRateLimiting`, and so on for every feature. They now live in Microsoft's namespaces,
  so delete `using ApiPipeline.NET.Extensions;`.
- **Option types renamed.** They are now `ApiPipeline*Options` (e.g. `CorsSettings` became
  `ApiPipelineCorsOptions`).
- **`UseApiPipeline` changed shape.** It now extends `IApplicationBuilder` and takes
  `Action<ApiPipelineBuilder>`. `IApiPipelineBuilder` is removed.
- **Removed APIs:**
  - all `Skip*()` methods
  - `ApiPipelineServiceRegistrationOptions` (the second argument of `AddApiPipeline`)
  - the single-feature `Use*()` methods
  - the request validation abstraction (`AddRequestValidation`, `WithRequestValidation`)
  - the public middleware types
- **Built-in exception handler removed.** `AddApiPipelineExceptionHandler()` and
  `WithExceptionHandler()` are gone. Register `AddProblemDetails()` / `UseExceptionHandler()` in your
  application.
- **`ApiPipeline.NET.OpenTelemetry` package removed.** Call `AddMeter(ApiPipelineTelemetry.MeterName)`
  in your own OpenTelemetry setup. The package's `ActivitySource`, public `Meter`, counters and
  `Record*()` methods are gone.
- **API version deprecation moved to `ApiPipeline.NET.Versioning`.** Its policies feed
  `Asp.Versioning`, which writes the headers. `WithVersionDeprecation()`, `AddApiVersionDeprecation()`
  and `IApiVersionReader` are removed. The package no longer depends on `ApiPipeline.NET`: it
  depends only on `Asp.Versioning.Http`, so it can be installed on its own. If you use
  `AddApiPipeline` and relied on getting `ApiPipeline.NET` transitively, add a direct reference.
- **Correlation IDs are no longer in `HttpContext.Items["X-Correlation-Id"]`.** Use
  `GetCorrelationId()` / `GetTraceId()`.

### Breaking: configuration

- **Startup now fails on configurations that never worked as written:** `StrictTransportSecurityPreload`
  without `StrictTransportSecurityIncludeSubDomains` or with a max-age under one year (the preload
  list refuses both); `*` listed with other `AllowedOrigins` (the framework compares it as a literal,
  so it matched nothing); an `http://` origin with `AllowCredentials` outside Development; and an
  edge or compression `ExcludedPaths` entry that does not start with `/`.
- **An empty JSON list (`[]`) is read as an empty list.** The binder skips it, so the default stayed:
  `ExcludedPaths: []` (rate limiting, edge, compression) kept the probe exclusions, contrary to the
  docs, and now replaces them; `AllowedMethods: []` / `AllowedHeaders: []` silently used the defaults,
  and now fail startup as an empty list always did. An empty environment variable for the key reads
  the same way.
- **Redis: `PartitionKeySecret` is required outside Development** when sharing is enabled.

- **Durations are `TimeSpan` settings**, written `"00:01:00"`, and lose their unit suffix, as on
  ASP.NET Core's own options (`KestrelServerLimits.KeepAliveTimeout`, `HstsOptions.MaxAge`):
  `WindowSeconds` → `Window` (rate limit policies and edge rate limiting),
  `StrictTransportSecurityMaxAgeSeconds` → `StrictTransportSecurityMaxAge`,
  `DefaultTimeoutSeconds` → `DefaultTimeout`, `RequestHeadersTimeoutSeconds` →
  `RequestHeadersTimeout`, `KeepAliveTimeoutSeconds` → `KeepAliveTimeout`,
  `MinRequestBodyDataRateGracePeriodSeconds` → `MinRequestBodyDataRateGracePeriod`, and on the Redis
  package `CommandTimeoutMilliseconds` → `CommandTimeout`, `FallbackCooldownSeconds` →
  `FallbackCooldown`, `SamplingDurationSeconds` → `SamplingDuration`,
  `ConcurrencyLeaseTimeoutSeconds` → `ConcurrencyLeaseTimeout`. The properties are `TimeSpan` in
  code too. An old key still present fails startup, naming the new one, and so does a bare number
  (`"Window": 60`), which a `TimeSpan` would read as 60 days. Rate-limit windows must be whole
  seconds, since `Retry-After` and `RateLimit-Policy` state them in seconds. Partition keys are
  unchanged for the same window, so shared (Redis) buckets survive the rename.
- **`ApiPipeline:Cors:AllowedOrigins` entries are format-checked.** With CORS enabled, an entry that
  can never match a browser's `Origin` header now fails startup: a
  trailing slash, a path, query or fragment, an explicit default port, a wildcard pattern such as
  `https://*.example.com`, the `null` origin, or a value that is not `scheme://host[:port]`. These
  entries were silently accepted before and never matched. The message gives the value to use.
  See [cors.md](docs/features/cors.md#origin-format).
- **Every section moved under one top-level `ApiPipeline` section**, and the child sections drop
  the `Options` suffix: `CorsOptions` became `ApiPipeline:Cors`, `RateLimitingOptions` became
  `ApiPipeline:RateLimiting`, `ApiVersionDeprecationOptions` became
  `ApiPipeline:VersionDeprecation`, and so on. The 1.x names were generic enough to collide with
  an application's own sections. The old sections are not read; startup logs a warning naming each
  one that has not moved. `ApiPipelineConfigurationKeys` values are now relative to the new
  `ApiPipelineConfigurationKeys.Section`.
- **`AddApiPipeline(IConfigurationSection)`** reads the pipeline settings from any section, e.g.
  `GetSection("Platform:Api")`. Every `IConfiguration` parameter (all `AddApiPipeline*` methods and
  `AddApiPipelineVersionDeprecation`) follows the same rule: a section is read as the pipeline section, and
  anything else is read through its `ApiPipeline` child.
- **`SuppressServerHeader` moved** from `ForwardedHeadersOptions` to `ApiPipeline:RequestLimits`. The
  old key is ignored.
- **Removed keys:** `ResponseCachingOptions:PreferOutputCaching`,
  `ApiVersionDeprecationOptions:PathPrefix`, and a policy's `AutoReplenishment`. Each had no effect;
  a leftover key is ignored.
- **New startup failures:**
  - API key partitioning without `KnownApiKeyHashes`
  - any invalid rate limit policy (e.g. `PermitLimit: 0`)
  - response caching and output caching both enabled
  - a deprecated version without `DeprecationDate`
  - `ProcessHost` without `AllowedHosts` (outside Development)
  - a feature's own section passed to its `AddApiPipeline*` method, e.g.
    `AddApiPipelineCors(configuration.GetSection("ApiPipeline:Cors"))` (throws at registration)
  - an unsafe pipeline order (see "The application decides the pipeline order", outside Development)
  - a `With*()` phase whose `AddApiPipeline*` registration is missing, e.g. `WithRateLimiting()`
    without `AddApiPipelineRateLimiting` (in every environment)
  - response compression enabled with both `EnableBrotli` and `EnableGzip` false (ASP.NET Core
    would otherwise use both)
- **Rate-limit responses:** a token bucket's `RateLimit-Policy` advertises its sustained rate
  (`TokensPerPeriod` per `Window`), not its burst size; an in-memory sliding-window 429 carries
  `Retry-After` of one segment; and `RejectionStatusCode` is 429, so an application's own
  `OnRejected` no longer turns rejections into 503s.
  - the app's own default CORS policy combined with `ApiPipeline:Cors:Enabled` (outside Development)
- **Configured CORS `AllowedMethods` / `AllowedHeaders` replace the defaults** instead of being
  appended to them.
- **Every setting is read once, at startup**, like ASP.NET Core's own middleware options. 1.x
  reloaded some values live (CORS lists, security header values, rate limits); 2.0 does not, so a
  configuration change needs a restart (roll the deployment). One rule for every setting, and an
  invalid edit can no longer reach a running process: it is never read, and the next start fails
  on it before serving traffic.

### Breaking: behavior

- **Startup safety guards apply in every environment except Development**, not only in one named
  `Production`. The forwarded-headers trust-set and `ProcessHost` checks and the CORS default-policy
  conflict check used `IsProduction()`, so an environment named `Prod`, `prd`, `PROD-EU` or `Staging`
  started with an unsafe configuration and only a warning. They now fail startup there too, and the
  error names the environment. The opt-out flag is renamed to match:
  `EnforceTrustedProxyConfigurationInProduction` is now `ApiPipeline:ForwardedHeaders:EnforceTrustedProxyConfiguration`.
- **`Cross-Origin-Resource-Policy: same-origin` is sent by default** (OWASP Secure Headers
  Project): another site cannot embed a response with a no-cors request. CORS requests are not
  affected. A response that sets its own value keeps it; set
  `ApiPipeline:SecurityHeaders:CrossOriginResourcePolicy` to `same-site`, `cross-origin`, or `""`.
- **`SunsetLink` is only the sunset link** (`rel="sunset"`); it was also sent as the deprecation
  link. Set the new `DeprecationLink` for `rel="deprecation"`. A `SunsetDate` earlier than the
  `DeprecationDate` now fails startup.
- **Rate-limit headers:** a concurrency policy's `RateLimit-Policy` now states
  `qu="concurrent-requests"`, per draft-ietf-httpapi-ratelimit-headers-11. The fields follow an
  Internet-Draft and may change when it becomes an RFC.
- **`Content-Security-Policy` is sent by default**: `default-src 'none'; frame-ancestors 'none'`, as
  the OWASP REST Security Cheat Sheet recommends for API responses (it was off). A response that
  sets its own CSP keeps it. HTML pages the API serves itself, such as Swagger UI, need their own
  policy; see [security-headers.md](docs/features/security-headers.md#content-security-policy-and-html-pages).
  Set `ApiPipeline:SecurityHeaders:ContentSecurityPolicy` to `""` to send none.
- **429s have no response body.** The application owns error bodies; `UseStatusCodePages()` restores
  a ProblemDetails body.
- **IETF rate-limit headers replace `X-RateLimit-*`.** The new fields are `RateLimit-Policy` and
  `RateLimit`. Both `EmitRateLimitHeaders` and `EmitRateLimitHeadersOnRejection` default to `false`.
- **Named rate limit policies are no longer also capped by the default policy.**
- **Security headers changed.**
  - `ApiPipeline:SecurityHeaders:Enabled` defaults to `true`.
  - The phase runs early, so rejections carry the headers too.
  - HSTS is written by ASP.NET Core's `UseHsts`: HTTPS only, never to `localhost`, `127.0.0.1` or
    `[::1]`.
- **Generated correlation IDs are random GUIDs**, not the trace ID. A new `X-Trace-Id` header carries
  the trace ID. The span tag was renamed from `correlation_id` to `apipipeline.correlation_id`.
- **API responses get `Cache-Control: no-store` by default.** Security headers now add it to any
  response that sets no `Cache-Control` of its own (OWASP REST Security Cheat Sheet). Endpoints meant
  to be cached set their own value, which wins; `ApiPipeline:SecurityHeaders:CacheControl` changes the
  default, `""` turns it off.
- **The application decides the pipeline order.** Phases run in the order the `With*()` methods are
  called; they are no longer re-sorted into a fixed order. When the pipeline is built, the order is
  checked against the recommended order's rules and every broken rule is logged as a warning naming
  both phases and the reason. A phase requested twice is applied once, with a warning. An app that
  called `With*()` out of the recommended order now runs in that order: check startup logs for
  `ApiPipeline.NET pipeline order:` warnings. Outside Development, a broken security rule fails
  startup instead (a cache before authentication or authorization, HTTPS redirection after
  authentication, forwarded headers after rate limiting or security headers, when both phases are
  enabled). CORS before the caches is a new rule, a warning.
- **`UseApiPipeline` calls `UseRouting()` itself**, right after forwarded headers.
- **Deprecation headers follow RFC 9745 and come from `Asp.Versioning`.**
  - The `Deprecation` value looks like `@1751328000`.
  - Responses that never reach an endpoint get no deprecation headers.
  - Dates apply on restart.
- **Metric changes.**
  - `apipipeline.request.body_bytes` is renamed to `apipipeline.request.body.size` (unit `By`), and the
    histogram now advises byte-sized bucket boundaries (256 B, then 1 KiB to 64 MiB in powers of 4).
  - Removed: `apipipeline.correlation_id.processed`, `apipipeline.exceptions.handled` and
    `apipipeline.deprecation.headers_added`.
  - OpenTelemetry naming: `apipipeline.ratelimit.rejected` is renamed to
    `apipipeline.rate_limiting.rejected` (spelled like ASP.NET Core's `aspnetcore.rate_limiting.*`),
    and the bare attributes are namespaced: `policy_name` becomes `apipipeline.rate_limiting.policy`,
    and `partition_type` becomes `apipipeline.rate_limiting.partition_type`.
    Prometheus series and labels change accordingly; see
    [migration.md](docs/migration.md#5-telemetry-changes).

### Fixed

- **Edge and compression `ExcludedPaths` entries are trimmed**, as the main limiter's were: an entry
  with surrounding spaces was never matched (edge) or threw while the pipeline was built (compression).
- **Redis: a fixed-window counter that lost its TTL no longer rejects forever.** The script set the
  expiry only on the first increment, so a `PERSIST`ed or restored key kept counting; it now gets a
  full window again on its next call.
- **Redis: the circuit's Warning (event 100) describes the fallbacks in use.** It always said "using
  per-pod limits", also to an operator whose policies fall back to `Allow` (no limit at all). It now
  begins `Shared rate limiting is unavailable until Redis responds.` and says what each configured
  fallback does; adjust log queries that matched the old text.
- **Redis: `IHttpContextAccessor` is no longer registered** for the synchronous-attempt diagnostic
  (event 103); it cost every request of the application an async-local write. An accessor the
  application registers is still used; without one, the diagnostic counts only unanswered attempts
  in a row.

- **A disabled `ApiPipeline:VersionDeprecation` section no longer fails startup.** `[Required]` on
  `DeprecatedVersion` was checked even with `Enabled: false`; every rule now applies only when enabled.
- **Configuration no longer dropped.** Package configuration was silently dropped when the
  application configured `ResponseCompressionOptions` or `ResponseCachingOptions` itself.
- **Rate limit policies added in code now work.** They were never registered, so
  `RequireRateLimiting` threw.
- **Rate limit policy entries are now validated.** `PermitLimit: 0` used to pass startup.
- **`AnonymousFallback = Reject` returns 429.** It used to return 500.
- **Sample and documented settings no longer model unsafe defaults.** The sample, the Kubernetes
  snippet, the README and the forwarded-headers and operations guides trusted all of `10.0.0.0/8`
  with `ForwardLimit: 2`, which lets any workload in the range set its own client IP; they now use
  a narrow placeholder range with `ForwardLimit: 1` and say to replace it with the proxies' own
  addresses. The sample's response caching is off outside Development, and its v1 `SunsetDate` is
  no longer in the past.
- **Compression's default `ExcludedPaths` matches the rate limiters': `/health` and
  `/internal/health`.** Probe responses under `/internal/health` were compressed.
- **Redis: a disposed limiter throws `ObjectDisposedException`**, as the framework's limiters do. It
  used to keep calling Redis.
- **Redis: the add-on's instruments are on the core package's `ApiPipeline.NET` meter.** It created
  a second meter of the same name, versioned from its own assembly.
- **Redis: comments corrected.** A background release is bounded by the client's `asyncTimeout`, not
  by a `WaitAsync` that cancelled nothing (removed); hashed keys are pseudonymous, not free of
  personal data; `Concurrency` rejections are writes.
- **A rate limit policy renamed by position fails startup.** Configuration merges lists by
  position, so an environment file listing `[per-user-burst]` over a base `[strict, permissive]`
  renamed `strict` (inheriting its other keys) and left `permissive` running. When two
  configuration sources name one `Policies` entry differently, startup now fails, naming both. The
  sample's `appsettings.Production.json` did exactly this; it now restates the base policies.
- **An `IRateLimitPartitionKeyResolver` that throws no longer fails the request with a 500.** It is
  skipped, as if it had returned `null`, and logged at `Error` (event 23): the request is partitioned
  by the next resolver or the built-in chain.
- **A slow first Redis connection costs one command timeout, not one per request.** While a pod's
  first connect was pending (up to `connectTimeout`, 5 s, with Redis down), every request waited
  `CommandTimeout` before falling back. Once one request has, the rest now fall back at once.
- **A Redis `Reject` fallback sends `Retry-After`.** Its 429s had none, so clients retried at once
  for the whole outage. It is now what is left of `FallbackCooldown` before Redis is asked again
  (1 s while the circuit is still closed).
- **Enum settings that name no member fail startup.** Configuration binds an enum from its number,
  so `"Fallback": "3"` (Redis) used to bind and act as `PerPod`, failing open where `Reject` was
  meant. The same applies to `PolicyFallbacks` values, `AnonymousFallback` (acted as a shared
  bucket), and a policy's `Kind` (failed every request) and `QueueProcessingOrder`.
- **`ApiPipeline:Cors:Enabled=false` keeps the app's own default CORS policy.** It used to discard it.
- **`/health` compression exclusions can be removed.**
- **Unbounded metric cardinality fixed.** Non-standard HTTP methods are now reported as `_OTHER`.

### Security

- **API key partitions require an allow-list.**
- **IPv6 clients are partitioned by `/64`.**
- **`X-Forwarded-Host` is ignored by default.**
- **Production startup fails on an empty or loopback-only trusted-proxy set.**
- **Compression over HTTPS stays opt-in** (`EnableForHttps: false`) because of BREACH/CRIME.
- **Redis partition keys are keyed hashes** (`PartitionKeySecret`): an unkeyed SHA-256 let anyone
  who could list the keys recover client IPv4 addresses.
- **CORS refuses `http://` origins with credentials outside Development.**

### Build

- **One version for every package**, in `src/Shared/Shared.props`, read by the baseline check and
  the release workflow; the XML documentation file is generated for the packages only, not tests.
- **The `Microsoft.SourceLink.GitHub` reference is removed.** The .NET SDK has included Source Link
  for GitHub since .NET 8.
- **Redis test images are pinned by digest** (`TestImages.cs`), so a moved tag cannot change the
  server the tests run on.
- **Markdown is linted in CI**, with markdownlint pinned in `package.json`/`package-lock.json` and
  kept current by Dependabot.
- **Reproducible restores.** Central transitive pinning (one version per package across the graph,
  which aligns the `Asp.Versioning.Http` that `Asp.Versioning.Mvc` pulls in to the pinned 10.2.3),
  committed `packages.lock.json` files, and locked-mode restores in every workflow. Six unused
  package pins removed.
- **Tests on xUnit v3 and Microsoft.Testing.Platform** (`global.json` sets the `dotnet test` runner),
  with Microsoft's code-coverage and TRX extensions replacing coverlet and VSTest. CI fails when line
  coverage drops below 85% (`ApiPipeline.NET`) or 75% (`ApiPipeline.NET.Versioning`).

- **Feed authentication.** CI, release and CodeQL workflows authenticate to the private feed.
- **API compatibility check.** Packing the core package validates its public API against 1.1.0.
  Intended breaks are recorded in `CompatibilitySuppressions.xml`.
- **The SDK is pinned** to 10.0.403 (`rollForward: latestPatch`, Dependabot proposes updates), so a
  build no longer changes with the runner image's SDK. 10.0.403 carries runtime 10.0.14, which fixes
  CVE-2026-111497 in the Brotli decoder.
- **Native AOT is checked in CI.** A job publishes one Native AOT app that uses all three packages,
  failing on any trimming or AOT warning (dependencies included), then runs the binary and checks its
  headers (`scripts/aot-smoke.sh`).
- **Shared source for the packages' plumbing.** The `Add*` idempotency claims and the startup-check
  host live once in `src/Shared` and are compiled into each package, which all run their startup
  checks the same way.
