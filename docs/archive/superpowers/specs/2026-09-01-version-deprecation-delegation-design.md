# Design: delegate API version deprecation to Asp.Versioning policies

> **Date:** 2026-09-01
> **Status:** Approved, pending implementation
> **Target release:** 2.0.0 (Unreleased — joins the existing breaking-change set)
> **Origin:** P1 finding of the 2026-09-01 architecture review

## 1. Problem

ApiPipeline.NET ships a custom API version deprecation subsystem: a middleware, a bespoke version
matcher, an abstraction over version readers, an options class with two custom validators, and a
satellite package supplying an `Asp.Versioning`-backed reader. Together they emit the `Deprecation`,
`Sunset`, and `Link` response headers.

`Asp.Versioning` 10 already does this. `ApiVersioningOptions.Policies` is an
`IApiVersioningPolicyBuilder` exposing `Deprecate(ApiVersion)` and `Sunset(ApiVersion)`, each
returning a policy builder with `Effective(DateTimeOffset)` and `Link(string)`.
`DefaultApiVersionReporter` takes `IPolicyManager<SunsetPolicy>` and
`IPolicyManager<DeprecationPolicy>` and writes the headers when `ReportApiVersions` is true. Both
`Deprecation` and `Sunset` header literals are present in `Asp.Versioning.Http.dll` 10.2.1.

The package is therefore maintaining ~500 lines that reimplement a dependency it already takes.

### The bug class this removes

`AspVersioningApiVersionReader` renders an `ApiVersion` to a string, and
`ApiVersionDeprecationMiddleware` re-parses both that string and the configured string to compare
them. `ApiVersionMatch` exists only to make that round-trip survive the several spellings of one
version (`1`, `1.0`, `v1`, `0001.0000`). Deleting the round-trip deletes the bug class: after this
change, a configured version is parsed once at startup by `ApiVersionParser` into the same
`ApiVersion` type the routing layer already matched on, and a version that does not parse fails the
host instead of silently never matching.

## 2. Decisions taken

| Decision | Choice | Rationale |
| --- | --- | --- |
| Release strategy | Straight removal in 2.0.0 | 2.0.0 is Unreleased; only v1.0.0 and v1.1.0 are tagged and published. CHANGELOG already declares 2.0.0 breaking with a migration guide. An `[Obsolete]` cycle would preserve compatibility with a version nobody has. |
| Configuration | Keep the `appsettings` section; add a thin binder | Operators keep editing deprecation dates without a code change and redeploy. This is the standardization value the package exists to add. |
| Package fate | `ApiPipeline.NET.Versioning` survives as the binder | The package id is published at 1.1.0. Retiring a published id is worse than repurposing it, and the feature needs a home that depends on `Asp.Versioning`. |
| Telemetry | Keep `apipipeline.deprecation.headers_added`, re-homed | "Callers are still on v1" is the actionable signal the whole feature exists to produce. Losing it would be a real regression. |

## 3. Architecture

### 3.1 Ownership

`ApiPipeline.NET` (core) loses all knowledge of API deprecation. It keeps no options class, no
configuration key, no middleware, no pipeline phase, and no version abstraction.

`ApiPipeline.NET.Versioning` owns the whole feature and is the only project that references
`Asp.Versioning`. Its single public entry point keeps its current signature:

```csharp
services.AddApiPipelineVersioning(configuration);
```

Called after the consumer's own `AddApiVersioning(...)`, so their reader, default version, and
`ReportApiVersions` choices are theirs.

### 3.2 Components

| Component | Type | Purpose |
| --- | --- | --- |
| `ApiVersionDeprecationOptions` | public options | Binds the `ApiVersionDeprecationOptions` section. `Enabled` + `DeprecatedVersions[]`. |
| `DeprecatedVersion` | public record/class | `Version`, `DeprecationDate`, `SunsetDate`, `SunsetLink`. |
| `ConfigureDeprecationPolicies` | `IConfigureOptions<ApiVersioningOptions>` | Translates bound options into `Policies.Deprecate(...)` / `Policies.Sunset(...)` calls. ~50 lines. |
| `VersionReportingStartupWarning` | `IHostedService` | Warns when policies are configured but the global `ReportApiVersions` is false. ~30 lines. Warns rather than throws — see §4.2. |
| `DeprecationReportingTelemetry` | `IReportApiVersions` decorator | Increments `apipipeline.deprecation.headers_added` after the inner reporter writes headers. ~35 lines. |

### 3.3 Data flow

```text
appsettings.json
  └─ ApiVersionDeprecationOptions section
       └─ AddOptions<ApiVersionDeprecationOptions>().Bind().Validate().ValidateOnStart()
            └─ ConfigureDeprecationPolicies : IConfigureOptions<ApiVersioningOptions>
                 └─ options.Policies.Deprecate(v).Effective(d).Link(u)
                    options.Policies.Sunset(v).Effective(d).Link(u)
                      └─ Asp.Versioning DefaultApiVersionReporter (framework)
                           └─ Deprecation / Sunset / Link / api-deprecated-versions headers
```

The binder runs as an `IConfigureOptions<ApiVersioningOptions>` registered after
`AddApiVersioning`, so options-pattern ordering guarantees the consumer's configure action runs
first and this one composes onto it rather than replacing it.

### 3.4 Packaging and the consumer install model

`ApiPipeline.NET` has zero runtime NuGet dependencies — a `FrameworkReference` on
`Microsoft.AspNetCore.App` and a private analyzer. It does not, and must not, reference
`ApiPipeline.NET.Versioning`. The arrow runs `Versioning → Core` only.

Deprecation headers are therefore an **explicit opt-in install**:

```bash
dotnet add package ApiPipeline.NET.Versioning
```

One command, not two: `ApiPipeline.NET` and the `Asp.Versioning.*` assemblies arrive transitively.
This is unchanged from today; what changes is that the satellite now owns the whole feature rather
than supplying a reader to a middleware in core.

Merging the feature into core was rejected. It would force `Asp.Versioning` onto every consuming
application — including minimal-API services that never version anything — to obtain correlation
IDs and security headers, which is the "too many dependencies" failure the architecture review
argued against.

#### Dependency reduction: `Asp.Versioning.Mvc` → `Asp.Versioning.Http`

`AspVersioningApiVersionReader` was the only reason the satellite referenced the MVC package.
Deleting it leaves every type the binder touches in two lighter assemblies:

| Type | Assembly |
| --- | --- |
| `ApiVersioningOptions`, `IReportApiVersions`, `DefaultApiVersionReporter` | `Asp.Versioning.Http` |
| `IApiVersioningPolicyBuilder`, `ApiVersionParser`, `SunsetPolicy`, `DeprecationPolicy` | `Asp.Versioning.Abstractions` (transitive via Http) |

So `ApiPipeline.NET.Versioning` drops to `Asp.Versioning.Http`, making it usable from a minimal-API
application with no MVC at all — which it currently is not.

Consequences, both of which are corrections rather than costs:

- **The sample must declare its own `Asp.Versioning.Mvc` reference.** It uses controllers and
  `.AddMvc()`, and currently receives the package transitively through the project reference. After
  the change the MVC dependency belongs to the application that chose MVC.
- **The test project is unaffected.** `AspVersioningIntegrationTests` versions minimal-API endpoints
  (`MapGet` + `MapToApiVersion`), which `Asp.Versioning.Http` supports.

`Directory.Packages.props` gains an `Asp.Versioning.Http` entry; `Asp.Versioning.Mvc` stays for the
sample.

### 3.5 Registration order is a contract

`AddApiPipelineVersioning(configuration)` **must** be called after `AddApiVersioning(...)`.

The binder itself is order-independent — `IConfigureOptions` composition does not care. The
telemetry decorator is not: it wraps the `IReportApiVersions` descriptor that `AddApiVersioning`
registers, so that descriptor has to exist first.

`AddApiPipelineVersioning` therefore throws immediately, with a message naming the fix, when
`IReportApiVersions` is absent from the service collection. This is a loud registration-time failure
rather than a silent one, which is the same trade the rest of the package makes.

## 4. Configuration contract

Unchanged except for the removal of `PathPrefix`:

```json
{
  "ApiVersionDeprecationOptions": {
    "Enabled": true,
    "DeprecatedVersions": [
      {
        "Version": "1.0",
        "DeprecationDate": "2025-07-01T00:00:00Z",
        "SunsetDate": "2026-01-01T00:00:00Z",
        "SunsetLink": "https://docs.example.com/api-v1-deprecation"
      }
    ]
  }
}
```

`PathPrefix` is deleted. It existed only because the middleware ran without endpoint knowledge and
had to guess which requests were API calls. `Asp.Versioning` resolves policies against the matched
endpoint's declared version, which is strictly more correct and needs no path heuristic.

Consumers who set `PathPrefix` must remove the key; the binder ignores unknown keys, so leaving it
is harmless but is called out in the migration guide.

### 4.1 Validation (fails startup)

| Rule | Reason |
| --- | --- |
| `Version` must parse via `ApiVersionParser.Default.TryParse` | Replaces the entire `ApiVersionMatch` bug class. An unparseable version previously produced silence; it now produces a startup failure naming the value. |
| `DeprecationDate` required | RFC 9745 defines the field value as a Structured Fields Date with no dateless form. Retained from the current implementation. |
| `SunsetLink` must be an absolute URI when present | Retained from the current implementation. |
| `SunsetDate` required when `SunsetLink` is set | New. A sunset link with no sunset date produces a `Link` header with nothing to link to. |

### 4.2 Startup warning (not a startup failure)

An earlier draft of this spec had `VersionReportingStartupGuard` **throw** when
`ApiVersioningOptions.ReportApiVersions` was false. A probe against `Asp.Versioning` 10.2.1 proved
that wrong.

Version reporting is enabled by **either** the global flag or a per-endpoint version set. Measured:

| `ApiVersioningOptions.ReportApiVersions` | version set `.ReportApiVersions()` | Headers emitted |
| --- | --- | --- |
| `true` | `false` | yes |
| `true` | `true` | yes |
| `false` | `true` | **yes** |
| `false` | `false` | no |

Throwing on the global flag alone would therefore fail startup for the third row — a correct,
common configuration where an application turns reporting on per version set. Failing a working
application is worse than the silence being guarded against.

Proving the fourth row at startup would mean walking `EndpointDataSource` and reading
`Asp.Versioning`'s endpoint metadata to determine per-set reporting. That is precisely the
framework-internal coupling this whole change exists to remove, for a diagnostic.

So `VersionReportingStartupWarning` **logs a warning** when deprecation policies are configured and
the global flag is false, stating that headers will appear only on endpoints whose version set calls
`.ReportApiVersions()`, and naming both remedies. No false failures, and the silent case still
leaves a trace in the log.

## 5. Telemetry

`ApiPipelineTelemetry.RecordDeprecationHeadersAdded` moves its call site out of core into
`DeprecationReportingTelemetry` in the Versioning package. The instrument itself stays in
`ApiPipelineTelemetry` so the meter and metric name are unchanged for existing dashboards.

The emitter is a decorator over `IReportApiVersions`, the service `Asp.Versioning` resolves to write
the headers:

```csharp
public void Report(HttpResponse response, ApiVersionModel apiVersionModel)
{
    _inner.Report(response, apiVersionModel);

    if (response.Headers.ContainsKey("Deprecation"))
    {
        ApiPipelineTelemetry.RecordDeprecationHeadersAdded(
            (response.HttpContext.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText);
    }
}
```

Chosen over a middleware because core no longer has a pipeline phase to host one, and adding a
`UseApiPipelineDeprecationTelemetry()` the consumer must remember to call would create exactly the
silent-failure mode this change is removing. The decorator also costs nothing on requests that do
not report versions — no `OnStarting` registration, no per-request allocation.

Semantics:

- Registered unconditionally by `AddApiPipelineVersioning`, not gated on `Enabled`.

  An earlier draft of this spec said the decorator is installed only when the feature is on. The
  implementation does not do that, and the simpler unconditional registration is what shipped. The
  cost when the feature is disabled is one virtual call and one `ContainsKey` per response that
  reports versions at all — nothing on a request that reports none. The visible consequence is that
  the ordering exception of §3.5 is thrown even for a consumer who has deprecation switched off,
  which is defensible: the ordering requirement is about registration, not about whether any policy
  is configured.
- Increments when the reporter wrote a `Deprecation` header.
- Tagged with `http.route` (the matched route **template**) only. Bounded by endpoint count.
- Delegates to the inner reporter first and unconditionally, so telemetry can never suppress a
  header. `RecordDeprecationHeadersAdded` must not throw; it is a counter increment.

The `api_version` tag is dropped. It was previously read from the configured version list, which the
new emitter does not consult; re-deriving it would mean re-introducing version matching, which is
the thing being deleted. `http.route` answers the operational question ("which endpoints are still
serving deprecated callers") at least as well.

## 6. RFC 9745 conformance — verified, no shim needed

The current middleware deliberately emits `Deprecation: @<unix-seconds>` per RFC 9745 §2, rejecting
the superseded `draft-dalal-deprecation-header` spellings (`true`, HTTP-date).

A probe against `Asp.Versioning` 10.2.1 confirms it emits the same conformant form. Measured output
for a policy effective `2025-07-01T00:00:00Z` with a sunset of `2026-01-01T00:00:00Z`:

```http
Deprecation: @1751328000
Sunset: Thu, 01 Jan 2026 00:00:00 GMT
Link: <https://docs.example.com/v1-sunset>; rel="sunset"; title="v1 sunset"; type="text/html",
      <https://docs.example.com/v1>; rel="deprecation"; title="v1 deprecation"; type="text/html"
api-deprecated-versions: 1.0
```

No shim is required.

The framework also emits a `rel="deprecation"` link the custom implementation does not.

**Correction (found during Task 7 implementation).** An earlier draft of this section claimed the
framework additionally emits `api-deprecated-versions` "for free". That over-generalised the probe,
which had marked the version deprecated in endpoint metadata. Two independent mechanisms are in
play:

| Header | Driven by |
| --- | --- |
| `Deprecation`, `Sunset`, `Link` | The **configuration-driven policies** this package registers. |
| `api-deprecated-versions` | **Endpoint metadata** — `[ApiVersion("1.0", Deprecated = true)]` or `.HasDeprecatedApiVersion(...)`. |

Configuration alone therefore produces the three RFC headers but not `api-deprecated-versions`; an
application that wants the latter must also mark the version deprecated in metadata. This is correct
framework behaviour, not a gap, but it must be documented — an operator who sets the config section
and sees `api-supported-versions` instead will otherwise read it as a bug. The sample declares both,
so it demonstrates the complete picture.

`Deprecation_HeaderValue_IsRfc9745StructuredFieldDate` is retained as a regression guard against a
future `Asp.Versioning` change.

## 6a. Behaviour change: short-circuited responses lose deprecation headers

Measured against `Asp.Versioning` 10.2.1:

| Response | Custom middleware (today) | Asp.Versioning (after) |
| --- | --- | --- |
| `200` from the endpoint | headers | headers |
| `401` from authorization | headers | **none** |
| `429` from rate limiting | headers | **none** |

`Asp.Versioning` reports versions only on responses that reach the selected endpoint. The custom
middleware registers a `Response.OnStarting` callback early in the pipeline, so it survives every
short-circuit — which is the reason `Phase.VersionDeprecation` is ordered ahead of CORS, auth, rate
limiting and the caches.

**This regression is accepted.** Deprecation is an advisory signal a client reads across many
successful calls, not something it acts on mid-throttle or while unauthenticated, and this is the
behaviour of every other `Asp.Versioning`-based API in the industry. The alternative — a shim
middleware the consumer has to remember to call — reintroduces exactly the silent-failure wiring
this change removes.

Consequences for the plan:

- `PipelineContractTests.VersionDeprecation_HeadersPresent_On429` and
  `...HeadersPresent_On401` are **deleted**. They pin behaviour that is being given up.
- A replacement test, `Deprecation_HeadersAbsent_OnShortCircuitedResponse`, pins the new behaviour
  so a future change to it is deliberate rather than accidental.
- `docs/migration.md` and `docs/features/version-deprecation.md` state it plainly, since an operator
  watching for deprecation headers on error responses will otherwise read this as a bug.

## 7. Removals

### Deleted files

| File | Lines |
| --- | ---: |
| `src/ApiPipeline.NET/Middleware/ApiVersionDeprecationMiddleware.cs` | 146 |
| `src/ApiPipeline.NET/Versioning/ApiVersionMatch.cs` | 80 |
| `src/ApiPipeline.NET/Versioning/IApiPipelineVersionReader.cs` | 19 |
| `src/ApiPipeline.NET/Options/ApiVersionDeprecationOptions.cs` | 63 (rewritten smaller in the Versioning package) |
| `src/ApiPipeline.NET.Versioning/AspVersioningApiVersionReader.cs` | 34 |
| `tests/ApiPipeline.NET.Tests/ApiVersionDeprecationMiddlewareTests.cs` | 151 |
| `tests/ApiPipeline.NET.Tests/ApiVersionMatchTests.cs` | ~90 |

### Removed public API (core)

- `ApiPipelineBuilder.WithVersionDeprecation()` and the `VersionDeprecation` phase
- `ApplicationBuilderExtensions.UseApiVersionDeprecation()`
- `ServiceCollectionExtensions.AddApiPipelineVersionDeprecation(IServiceCollection, IConfiguration)`
- `ServiceCollectionExtensions.ConfigureApiVersionDeprecation` (internal) and its two validators
- `ApiPipelineServiceRegistrationOptions.AddApiPipelineVersionDeprecation`
- `ApiPipelineConfigurationKeys.ApiVersionDeprecation`
- `ApiPipeline.NET.Versioning.IApiPipelineVersionReader`
- `ApiPipeline.NET.Options.ApiVersionDeprecationOptions` / `DeprecatedVersion` (moved namespace)

### Changed public API (core)

`ApiPipelineTelemetry.RecordDeprecationHeadersAdded(string? apiVersion = null)` becomes
`RecordDeprecationHeadersAdded(string? route = null)`, and the emitted tag changes from
`api_version` to `http.route` (§5). The metric name `apipipeline.deprecation.headers_added` and the
meter are unchanged. Dashboards that group by `api_version` need updating; this joins the metric
rename already recorded for 2.0.0 in the CHANGELOG.

**Net: ~700 lines deleted (including the two short-circuit tests of §6a), ~130 added.**

## 8. Testing

Test-driven: the rewritten integration tests are written before the deletion lands, against the new
binder, and must fail for the right reason first.

### Rewritten

`AspVersioningIntegrationTests` drives real requests through `AddApiVersioning` + the binder and
asserts headers on the wire, replacing tests that asserted middleware internals.

### New behavioural tests

| Test | Asserts |
| --- | --- |
| `Deprecation_HeaderValue_IsRfc9745StructuredFieldDate` | The characterization test of §6. |
| `DeprecatedVersion_Request_EmitsDeprecationSunsetAndLink` | All three headers on a call to a deprecated version. |
| `NonDeprecatedVersion_Request_EmitsNoDeprecationHeaders` | No false positives. |
| `Deprecation_HeadersAbsent_OnShortCircuitedResponse` | The accepted regression of §6a, pinned so a future change to it is deliberate. |
| `Disabled_EmitsNothing` | `Enabled=false` is honoured. |
| `UnparseableVersion_FailsStartup` | Replaces the silent-mismatch failure mode. |
| `SunsetLinkWithoutSunsetDate_FailsStartup` | New validator. |
| `ReportApiVersionsFalse_WithPolicies_LogsWarning` | The startup warning fires, and startup still succeeds. |
| `ReportApiVersionsFalse_PerEndpointSetTrue_StillEmitsHeaders` | The configuration §4.2 must not fail: reporting enabled per version set works. |
| `DeprecationTelemetry_TagsRouteTemplateOnly` | Bounded cardinality; no caller-controlled dimension. |
| `DeprecationTelemetry_DoesNotSuppressHeaders` | The decorator delegates to the inner reporter unconditionally. |
| `AddApiPipelineVersioning_BeforeAddApiVersioning_Throws` | The ordering contract of §3.5 fails loudly. |
| `CustomReportApiVersions_IsStillInvoked` | Decoration preserves a consumer-supplied reporter. |
| `VersionSpellingVariants_AllMatch` | `v1`, `1`, `1.0` against a configured `1.0` — the invariant `ApiVersionMatch` used to hold, now held by `ApiVersionParser` + routing. |

### Updated

`TestAppBuilder` (drop `AddApiPipelineVersionDeprecation`), `PipelineContractTests` (phase list),
`ApiPipelineSmokeTests`, `TelemetryCardinalityTests` (deprecation counter tags).

## 9. Documentation

| File | Change |
| --- | --- |
| `docs/features/version-deprecation.md` | Rewritten around the binder and `Asp.Versioning` policies, including the short-circuit behaviour change of §6a. |
| `docs/migration.md` | New section: config key removal, removed public API, required `AddApiVersioning` ordering, how to enable version reporting (global flag or per version set), and the short-circuit behaviour change of §6a. |
| `CHANGELOG.md` | Breaking-change entries under the existing 2.0.0 Unreleased heading. |
| `README.md` | Feature bullet, wiring snippet, pipeline example (drop `WithVersionDeprecation()`). |
| `docs/ARCHITECTURE.md`, `docs/INTERNALS.md` | Component inventory and phase list. |
| `docs/RUNBOOK.md`, `docs/TROUBLESHOOTING.md` | "No deprecation headers" now points at `ReportApiVersions` (global or per version set) and the startup warning. |
| `samples/.../Program.cs` | Drop `.WithVersionDeprecation()`; show `ReportApiVersions = true`. |
| `samples/.../appsettings*.json` (3), `ConfigurationSnippets/minimal.json`, sample `README.md` | Drop `PathPrefix`. |
| `README.md`, `docs/features/version-deprecation.md` | State the install model of §3.4: `ApiPipeline.NET.Versioning` is a separate opt-in package, not a transitive dependency of core. |

## 10. Project file changes

| File | Change |
| --- | --- |
| `src/ApiPipeline.NET.Versioning/ApiPipeline.NET.Versioning.csproj` | `Asp.Versioning.Mvc` → `Asp.Versioning.Http`. |
| `samples/.../ApiPipeline.NET.Sample.csproj` | Add explicit `Asp.Versioning.Mvc` (was transitive; the sample uses controllers and `.AddMvc()`). |
| `Directory.Packages.props` | Add `Asp.Versioning.Http` 10.2.1; keep `Asp.Versioning.Mvc` for the sample. |

## 11. Risks

| Risk | Mitigation |
| --- | --- |
| `Asp.Versioning` emits a non-RFC-9745 `Deprecation` value | Closed by measurement (§6): it emits `@<unix-seconds>`. A regression test pins it. |
| Startup diagnostic fails a working application | Closed by measurement (§4.2): the guard warns instead of throwing, because per-endpoint version sets can enable reporting independently of the global flag. |
| A consumer relies on deprecation headers appearing on 401/429 | Accepted and documented (§6a). Two tests that pinned the old behaviour are deleted and replaced by one pinning the new. |
| Policies are keyed by API *name* + version; named APIs behave differently from unnamed | Binder uses the unnamed overload, matching the common case. Documented; a named-API overload is out of scope. |
| Consumer forgets to enable version reporting entirely | `VersionReportingStartupWarning` logs a warning naming both remedies (§4.2). Not a hard failure, because per-endpoint version sets can enable reporting without the global flag. |
| Consumer calls `AddApiPipelineVersioning` before `AddApiVersioning` | The binder is order-independent, but the telemetry decorator is not (§3.5). `AddApiPipelineVersioning` throws at registration time with a message naming the fix. |
| A consumer supplied their own `IReportApiVersions` | The decorator wraps whatever descriptor is present and delegates to it, so a custom reporter keeps working. |
| Config-shape break for v1.1.0 consumers | Only `PathPrefix` is removed; every other key binds unchanged. Documented in `migration.md`. |

## 12. Out of scope

- Named-API deprecation policies (`Deprecate(string name, ApiVersion)`).
- Per-endpoint deprecation attributes — already `Asp.Versioning`'s
  `[ApiVersion("1.0", Deprecated = true)]`, and the package should not wrap it.
- The remaining P2/P3 findings of the architecture review.
