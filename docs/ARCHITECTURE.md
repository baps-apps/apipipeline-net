# Architecture

How the packages work inside. Using them needs only the [README](../README.md) and the feature
guides.

`ApiPipeline.NET` is a thin layer over ASP.NET Core's middleware. It registers the framework
features with validated options, applies them in the order the app chooses and checks that order,
and adds what the framework lacks: correlation IDs, the non-HSTS security headers and the edge
limiter.

## Packages

| Package | Contains | Depends on |
| --- | --- | --- |
| `ApiPipeline.NET` | Options, pipeline builder, middleware, metrics | ASP.NET Core shared framework |
| `ApiPipeline.NET.Versioning` | Deprecation policies for `Asp.Versioning` | `Asp.Versioning.Http` |
| `ApiPipeline.NET.RateLimiting.Redis` | Limiters backed by Redis | `ApiPipeline.NET`, `StackExchange.Redis` |

## Source layout

```text
src/Shared/                       Compiled into every package: startup checks, Add* claims,
                                  duration and enum validation
src/ApiPipeline.NET/
  Extensions/                     AddApiPipeline*, UseApiPipeline (Microsoft namespaces)
  Pipeline/                       Builder, ordering rules, one Use* per phase
  Options/                        Options types, validators, framework option configurators
  Configuration/                  Section names, empty-list binding, StrictEnvironment
  Middleware/                     The package's own middleware (internal)
  RateLimiting/                   Global limiter, partition keys, factories, 429 writer
  Cors/ ForwardedHeaders/         CORS provider and guards; proxy trust
  Correlation/ Observability/     Header names, accessors; metrics, log messages
src/ApiPipeline.NET.Versioning/   Deprecation policies, startup warnings
src/ApiPipeline.NET.RateLimiting.Redis/  Lua-scripted limiters, circuit, connection
```

## Pipeline composition

- Phases run in call order. [`PipelineOrderRules`](../src/ApiPipeline.NET/Pipeline/PipelineOrderRules.cs)
  holds "A before B, because…" rules; a broken rule is logged, a broken security rule fails startup
  outside Development when both phases add middleware. `WithMiddleware` steps are not checked.
- Each phase reads its feature's `Enabled` once, when the pipeline is built; a repeated phase is
  applied once.
- Most phases are ASP.NET Core's middleware. The package's own: correlation IDs, request size
  tracking, security headers (except HSTS) and the edge limiter.
- The pipeline adds routing after forwarded headers; a later `UseRouting()` is a no-op.
- A phase without its `AddApiPipeline*` registration fails the build.

## Configuration

- **One section.** Every feature reads a child of `ApiPipeline`. An `IConfigurationSection`
  argument is read as that section itself. A feature's own section (`ApiPipeline:Cors`,
  `ApiPipeline:VersionDeprecation`, `ApiPipeline:RateLimiting:Redis`) throws `ArgumentException` in
  all three packages: it would bind nothing (`src/Shared/PipelineSection.cs`).
- **One options source.** Configuration, `Action<TOptions>` and `Configure<TOptions>` feed the same
  options, validated by source-generated validators with `ValidateOnStart()`. An empty JSON list
  binds as empty (`EmptyListBinding`).
- **Settable, nullable lists.** List options are `IList<T>?` with a setter, which the Framework
  Design Guidelines (CA2227) would make get-only. Here `null` and empty mean different things:
  `null` (key absent) applies the package default, such as `ExcludedPaths` → `/health`,
  `/internal/health`, while `[]` deliberately means none. A get-only list can't express the
  difference, so the setter stays. In code, assign a list to replace the default
  (`o.ExcludedPaths = ["/live"]`), or leave it `null` to keep it.
- **Read once**, through `IOptions<T>`.
- **Idempotent registration.** Every `AddApiPipeline*` goes through `AddFeature`: the section is
  bound once (twice would duplicate list entries), `Action<TOptions>` applies on every call,
  services register once. A different section for the same feature throws.

### What registration does when a feature is disabled

| Registration | Disabled | Enabled |
| --- | --- | --- |
| Request limits | Kestrel `AddServerHeader = false` only (unless `SuppressServerHeader: false`) | Kestrel, IIS, HTTP.sys and form limits |
| CORS | `AddCors()`; wraps the existing `ICorsPolicyProvider`, which answers every lookup | Default lookup from `ApiPipeline:Cors`; named lookups to the wrapped provider |
| Rate limiting | `AddRateLimiter()` only; your own settings untouched | `GlobalLimiter`, `OnRejected`, one policy per name |
| Compression, caching, HSTS, timeouts | The framework service only | The framework options from configuration |
| Any | `AddMetrics()`, `AddRouting()`, the startup-check service | Same |

Services are registered either way: `Enabled` is unknown until the container is built.

### Framework options: your settings win

What the package sets on framework options (Kestrel, IIS, HTTP.sys, forms, HSTS, compression,
output and response caching, request timeouts) is a default: its configurator is inserted first,
so anything the application sets on the same options, before or after `AddApiPipeline`, wins
property by property.

```csharp
builder.Services.AddHsts(o => o.MaxAge = TimeSpan.FromDays(30)); // wins, in either order
builder.Services.AddApiPipeline(builder.Configuration);          // the other HSTS settings
```

Set anything the package doesn't cover directly on the framework options. Kestrel doesn't bind
`Kestrel:Limits` itself; bind it with `Configure<KestrelServerOptions>(config.GetSection("Kestrel"))`
to have it win. Rate limiting, CORS and forwarded headers are the package's own features, not
defaults; forwarded headers ignore application configuration so their trust checks can't be bypassed.

### Startup

Each package has one hosted service (`src/Shared/StartupChecks.cs`) that runs its
`IApiPipelineStartupCheck`s in registration order, in `IHostedLifecycleService.StartingAsync`. The
host finishes every `StartingAsync`, and stops on an exception, before any `StartAsync` starts the
server: a failed check means the server never listens, in any registration order or with
`ServicesStartConcurrently`. Checks needing listen addresses register on `ApplicationStarted`.
Guards fail startup in every environment but Development (`StrictEnvironment`).

## Request path

- Correlation and security headers are written in `Response.OnStarting`, so short-circuited and
  error responses get them; existing values are kept.
- `RateLimitRejection` writes every 429, from both limiters.
- `DefaultPolicy` and `GlobalPolicy` share the framework's `GlobalLimiter`
  (`PartitionedRateLimiter.CreateChained`); the default steps aside for an endpoint policy.
- Partition keys include a SHA-256 signature of the policy's limits, identical across processes.
- The edge limiter is a plain `PartitionedRateLimiter`, not a second `UseRateLimiter()`.
- Partition limiters come from `IApiPipelineRateLimiterFactory`: in-memory by default, Redis with
  the add-on. The edge limiter always stays in memory.
- Metrics come from the host's `IMeterFactory`. No `ActivitySource`: the request span is tagged.

## Extension points

`AddApiPipeline*` per feature; `WithMiddleware`; `IEndpointFilter` for request checks;
`PartitionClaims` or `IRateLimitPartitionKeyResolver`; `IApiPipelineRateLimiterFactory`; named CORS
policies via `AddCors`/`RequireCors`.

## Decisions

### HSTS is delegated to `HstsMiddleware`

The framework's `UseHsts()` writes HSTS outside Development; the package writes the other seven
headers. Consequences: no HSTS over HTTP (browsers ignore it there), and an exception handler that
clears the response drops it from that response (browsers keep the policy).

### Dependencies declined

- **NetEscapades.AspNetCore.SecurityHeaders**: a first runtime dependency to replace ~200 tested
  lines.
- **Microsoft.AspNetCore.HeaderPropagation**: copies the inbound header only, so generated IDs
  wouldn't propagate ([details](features/correlation-id.md#why-not-microsoftaspnetcoreheaderpropagation)).
- **Polly for the Redis circuit**: Polly 8 can't open on consecutive failures, treats a cancelled
  probe as success, and can't record outcomes without gating (needed for `Reject` policies).
