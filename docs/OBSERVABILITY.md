# Observability

How ApiPipeline.NET's traces and metrics reach your observability backend, why the wiring is a
**two-call** setup, and the full trace-tag / metric reference.

## TL;DR

```csharp
using ApiPipeline.NET.OpenTelemetry;   // AddApiPipelineTelemetrySources
using OpenTelemetry.NET.Extensions;    // AddObservability

var builder = WebApplication.CreateBuilder(args);

builder.AddObservability();               // OpenTelemetry.NET — host bootstrap (once per process)
builder.AddApiPipelineTelemetrySources(); // ApiPipeline.NET — registers its ActivitySource + Meter
```

Two calls, in this order. `AddObservability()` first (builds the export pipeline), then
`AddApiPipelineTelemetrySources()` (plugs ApiPipeline.NET's signals into it).

- `AddObservability()` (from the **OpenTelemetry.NET** package) builds the export pipeline — SDK, exporters,
  resource attributes, log enrichment — and watches the usual sources (ASP.NET Core, `HttpClient`, runtime).
- `AddApiPipelineTelemetrySources()` (from **ApiPipeline.NET.OpenTelemetry**) adds the `ApiPipeline.NET`
  source/meter to that pipeline so pipeline traces and metrics are actually exported.
- **Logs need no extra wiring**: pipeline middleware writes through `ILogger`, which the host logging
  pipeline (configured by `AddObservability`) already captures.

## Why two calls

### Separation of concerns

Bootstrapping observability is the **OpenTelemetry.NET** package's responsibility, not ApiPipeline.NET's.
ApiPipeline.NET only *contributes its own signals* on top of whatever bootstrap the host chose. Keeping the
two apart means:

- One package owns the SDK lifecycle, exporters, and config (`AppSettings` / `OpenTelemetryOptions`).
- ApiPipeline.NET stays a good citizen — it never re-initializes the SDK behind your back.

`AddObservability()` may only run **once per process** (it initializes process-wide state such as
`SensitiveDataConstants`). If a helper called it for you *and* you called it yourself, the second call throws:

```text
System.InvalidOperationException: SensitiveDataConstants.Initialize has already been called in this process.
AddObservability may only be called once per process lifetime.
```

By making source registration a separate, side-effect-free call, you bootstrap exactly once and add
ApiPipeline.NET's signals without that hazard.

### The OpenTelemetry SDK is opt-in *by source name*

This is the core mechanic. The .NET diagnostics APIs (`ActivitySource`, `Meter`) emit unconditionally —
the producing code doesn't know or care whether anyone is listening. The OpenTelemetry SDK only **records
and exports signals from sources it was explicitly told to watch**, by name:

```csharp
tracerProvider.AddSource("ApiPipeline.NET");   // traces
meterProvider.AddMeter("ApiPipeline.NET");     // metrics
```

`AddApiPipelineTelemetrySources()` is exactly those two registrations:

```csharp
builder.Services.ConfigureOpenTelemetryTracerProvider(t => t.AddSource(ApiPipelineTelemetry.ActivitySourceName));
builder.Services.ConfigureOpenTelemetryMeterProvider(m => m.AddMeter(ApiPipelineTelemetry.MeterName));
```

Both `ActivitySourceName` and `MeterName` are the constant `"ApiPipeline.NET"`.

If the source is **not** registered, every `Activity` and metric ApiPipeline.NET middleware emits is dropped
on the floor by the SDK — no error, no warning, just silence.

### Mental model

```text
AddObservability()               → SDK + exporters running. Watches: ASP.NET Core, HttpClient, runtime.
                                   ApiPipeline.NET signals emitted but UNWATCHED → dropped.
AddApiPipelineTelemetrySources() → adds "ApiPipeline.NET" to the watch list → signals now exported.
```

`AddObservability()` lays the pipe. `AddApiPipelineTelemetrySources()` connects ApiPipeline.NET's faucet to
it. Skip the second call and pipeline telemetry exists in-process but never leaves.

## Real-world scenario: a throttled client

SPM.Api runs behind an ingress. A client hammers an endpoint and trips the `strict` rate-limit policy.

### Without `AddApiPipelineTelemetrySources()`

- `AddObservability()` exports the incoming HTTP server span and runtime metrics — that part works.
- The client gets `429`. The pipeline calls `RecordRateLimitRejected("strict", "user")`, incrementing the
  `apipipeline.ratelimit.rejected` counter, and sets the `correlation_id` tag on the current activity.
- The SDK never registered the `ApiPipeline.NET` meter/source → **the counter increment and the tag are
  dropped.**
- Result: your "rate-limit rejections" dashboard reads a flat zero, and traces have no `correlation_id` to
  pivot on. You are blind to pipeline behavior during an incident.

### With `AddApiPipelineTelemetrySources()`

- Same `429`, but the SDK now watches `ApiPipeline.NET`.
- `apipipeline.ratelimit.rejected{policy_name=strict,partition_type=user}` is exported to Prometheus → an
  alert fires: "user X is being throttled."
- The incoming request span carries `correlation_id=<id>` → search Tempo/Jaeger by that ID and follow the
  single request across SPM.Api and into downstream services.
- Other pipeline signals become visible too: `apipipeline.exceptions.handled`,
  `apipipeline.deprecation.headers_added`, `apipipeline.cors.rejected`, and the
  `apipipeline.request.body_bytes` histogram.

## What ApiPipeline.NET exports

Everything below comes from a single `ActivitySource` and a single `Meter`, both named **`ApiPipeline.NET`**.
This is the complete, exhaustive list — the package emits nothing else.

### Traces

ApiPipeline.NET **creates no spans of its own.** Instead it enriches the **current** activity (typically the
ASP.NET Core server span produced by `AddObservability()`'s instrumentation) with tags, so they ride along
with the request trace you already export.

The only span attribute the package sets:

| Tag | Set by | Value |
|---|---|---|
| `correlation_id` | `CorrelationIdMiddleware` (via `ApiPipelineTelemetry.SetCorrelationIdOnCurrentActivity`) | The resolved correlation ID — propagated from the client `X-Correlation-Id` header, or generated when absent |

> Because there are no custom spans, ApiPipeline.NET adds **no extra entries to your trace waterfall** — it
> only annotates the existing request span. If you see no `correlation_id` on traces, the source isn't
> registered (see [Troubleshooting](#troubleshooting)).

### Metrics

Six instruments on the `ApiPipeline.NET` meter (5 counters + 1 histogram). Instrument names follow the
`apipipeline.*` convention.

| Metric | Type | Unit | Dimensions | Emitted by | When emitted |
|---|---|---|---|---|---|
| `apipipeline.ratelimit.rejected` | Counter | — | `policy_name`, `partition_type` (`user` \| `ip` \| `anonymous` \| `unknown`) | Rate limiting (`AddRateLimiting` rejection callback) | A request is rejected by rate limiting (429) |
| `apipipeline.deprecation.headers_added` | Counter | — | `apipipeline.api_version` | `ApiVersionDeprecationMiddleware` | A response includes API version deprecation headers |
| `apipipeline.correlation_id.processed` | Counter | — | — | `CorrelationIdMiddleware` | A correlation ID is generated or propagated |
| `apipipeline.exceptions.handled` | Counter | — | — | Exception handler (`UseApiPipeline` exception handling) | An unhandled exception is caught by the pipeline handler |
| `apipipeline.cors.rejected` | Counter | — | — | `LiveConfigCorsPolicyProvider` | A CORS request is rejected (origin not in `AllowedOrigins`) |
| `apipipeline.request.body_bytes` | Histogram | `bytes` | — | `RequestSizeMiddleware` | Incoming request body size, sampled from `Content-Length` when present |

> Dimensions on the rate-limit and deprecation counters are only attached when the corresponding value is
> available; otherwise the counter is incremented without them.

### Logs

ApiPipeline.NET does not define its own log source — middleware writes through the standard `ILogger<T>`,
captured by the host logging pipeline that `AddObservability()` configures. Nothing to register here.

## Backend setup

`AddApiPipelineTelemetrySources()` only registers the source/meter — it does not choose an exporter. The
exporter and all SDK configuration come from `AddObservability()` (`AppSettings` / `OpenTelemetryOptions`
sections, plus code-based overrides). See the OpenTelemetry.NET package docs for the configuration surface.

Typical backends for the exported traces and metrics:

- **Traces**: Grafana Tempo, Jaeger, Azure Monitor / Application Insights.
- **Metrics**: Prometheus (scrape or OTLP), Azure Monitor.

In Kubernetes, point the OTLP exporter at your collector and rely on the `correlation_id` trace tag for
cross-service correlation. See [OPERATIONS.md](OPERATIONS.md) for deployment and probe configuration.

## Troubleshooting

| Symptom | Likely cause | Fix |
|---|---|---|
| `InvalidOperationException: ... AddObservability may only be called once per process` | `AddObservability()` called twice (e.g. directly *and* via a helper) | Call `AddObservability()` exactly once, then `AddApiPipelineTelemetrySources()` |
| ASP.NET Core spans export but no `apipipeline.*` metrics, no `correlation_id` tag | `AddApiPipelineTelemetrySources()` not called — source unregistered | Add `builder.AddApiPipelineTelemetrySources();` after `AddObservability()` |
| Nothing exports at all | `AddObservability()` not called, or no exporter configured | Bootstrap with `AddObservability()` and configure an exporter |

## Related

- [ARCHITECTURE.md](ARCHITECTURE.md) — pipeline design and middleware ordering
- [INTERNALS.md](INTERNALS.md) — telemetry internals and the `ApiPipelineTelemetry` API
- [OPERATIONS.md](OPERATIONS.md) — production deployment and monitoring
