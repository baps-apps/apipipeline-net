# Observability

The package emits metrics on one `Meter` (`ApiPipeline.NET`), tags the request span with
`apipipeline.correlation_id`, scopes request logs with `CorrelationId`, and logs startup findings.
It creates no spans and does not configure OpenTelemetry.

## Wiring

```csharp
builder.Services.AddOpenTelemetry()
    .WithTracing(t => t.AddAspNetCoreInstrumentation().AddOtlpExporter())
    .WithMetrics(m => m
        .AddAspNetCoreInstrumentation()
        .AddMeter(ApiPipelineTelemetry.MeterName)
        .AddOtlpExporter());

// Or, when OpenTelemetry is configured elsewhere:
builder.Services.ConfigureOpenTelemetryMeterProvider(m => m.AddMeter(ApiPipelineTelemetry.MeterName));
```

Without `AddMeter` the metrics are never exported.

### Metrics emitted

| Metric | Type | Attributes | Recorded when |
| --- | --- | --- | --- |
| `apipipeline.rate_limiting.rejected` | Counter | `…policy`, `…partition_type` (`user`, `apikey`, `ip`, `anonymous`, `claim`, resolver type) | Main limiter returns 429 |
| `apipipeline.edge_rate_limiting.rejected` | Counter | | Edge limiter returns 429 |
| `apipipeline.rate_limiting.partitions.overflow` | Counter | `…partition_type` (`ip`, `identity`) | `MaxIpPartitions` or `MaxIdentityPartitions` reached |
| `apipipeline.edge_rate_limiting.partitions.overflow` | Counter | | Edge `MaxIpPartitions` reached |
| `apipipeline.rate_limiting.store.fallback` | Counter | `…policy`, `…store.fallback_reason` (`timeout`, `error`, `circuit_open`, `connecting`) | Redis: a request decided without Redis |
| `apipipeline.rate_limiting.store.duration` | Histogram (s) | `…policy`; `error.type` when Redis didn't answer | Redis: each call (not while the circuit is open) |
| `apipipeline.rate_limiting.store.degraded` | Gauge (0/1) | | Redis: 1 while the circuit is open |
| `apipipeline.cors.rejected` | Counter | | `Origin` not allowed |
| `apipipeline.correlation_id.rejected` | Counter | | Invalid inbound `X-Correlation-Id` replaced |
| `apipipeline.request.body.size` | Histogram (bytes) | `http.request.method`, `http.route` | `WithRequestSizeTracking()`, `Content-Length` > 0 |

`…` stands for `apipipeline.rate_limiting.`. Prometheus replaces dots with underscores and adds
`_total` to counters: `apipipeline_rate_limiting_rejected_total{apipipeline_rate_limiting_partition_type="ip"}`.
Attribute values are bounded (configuration or fixed sets; unknown methods are `_OTHER`); paths,
users, keys and IDs are never attributes. Alerts: [OPERATIONS.md](OPERATIONS.md#monitoring).

`apipipeline.rate_limiting.rejected` and ASP.NET Core's `aspnetcore.rate_limiting.requests` count
the same rejections; never add them. Use the framework's series for rates and the package's for the
partition type and the default policy's name. The edge limiter has only its own counter.

### Dashboards and alerts with the Redis package

A Redis limiter always takes the framework's asynchronous ("queued") path, so with the Redis
package `aspnetcore.rate_limiting.queued_requests` is never 0 under load and
`request.time_in_queue` is the Redis round trip. Don't alert on them; chart
`store.duration`, `store.degraded` and `store.fallback` instead.

## Traces

`apipipeline.correlation_id` on ASP.NET Core's request span holds the correlation ID: search traces
by it. Responses carry the trace ID in `X-Trace-Id` (unless `EmitTraceIdHeader: false` or there is no
activity). No tracing, no tag.

## Logging

`WithCorrelationId()` opens a scope with `CorrelationId`; the host adds `TraceId`. Your provider
must include scopes (console: `IncludeScopes: true`).

Event IDs are stable; match on them, not the text. Core 1–99, Redis 100–199, Versioning 200–299.

| Event ID | Level | Message starts with | Meaning |
| --- | --- | --- | --- |
| 1 | Debug | `Rejected invalid correlation ID` | Malformed `X-Correlation-Id` replaced |
| 10 | Information | `ApiPipeline.NET pipeline composed` | Phases in run order; check first when a feature "does nothing" |
| 11 | Warning | `... was requested more than once` | Repeated `With*()`; runs once |
| 12 | Warning | `... runs before ...` | Ordering rule broken; message says why |
| 13 | Warning | `...:Enabled is true, but its phase was never added` | Feature on, phase missing |
| 14 | Warning | `UseApiPipeline(...) was never called` | Features on, no pipeline |
| 15 | Warning | `With... is in the pipeline, but ...:Enabled is false` | Phase requested, feature off |
| 20 | Information | `Edge rate limiting enabled` | |
| 21 | Information | `Rate limiting enabled` | Lists policies |
| 22 | Warning | `Rate limiting: the server is listening on` | [Socket listener](features/rate-limiting.md#unix-sockets-and-named-pipes) rejects anonymous requests |
| 23 | Error | `Rate limiting: the partition key resolver` | A resolver threw; request partitioned by the built-in chain |
| 24 | Warning | `... client-address partitions are live` | `MaxIpPartitions` reached (at most once a minute) |
| 25 | Warning | `... identity partitions ... are live` | `MaxIdentityPartitions` reached (at most once a minute) |
| 30 | Warning | `ResponseCompression: EnableForHttps is true` | BREACH reminder |
| 31 | Information | `ResponseCompression is enabled but EnableForHttps is false` | HTTPS responses not compressed |
| 40 | Warning | `CORS: AllowAll policy is active` | Development allow-all |
| 42 | Warning | `CORS: this application registered its own default policy` | Replaced (Development; elsewhere fails startup) |
| 43 | Warning | `CORS: ... an ICorsPolicyProvider` | Later provider replaces the pipeline's (Development; elsewhere fails) |
| 44 | Warning | `CORS: the application's own ICorsPolicyProvider` | Your provider is wrapped |
| 52 | Warning | `ForwardedHeaders is enabled` or `ApiPipeline:ForwardedHeaders:` | Trust or host problem; fails startup outside Development |
| 62 | Warning | `ApiPipeline.NET 2.0 reads its settings from` | 1.x top-level sections present ([migration](migration.md#3a-every-section-moved-under-apipipeline)) |
| 70 | Warning | `Request limits: this application runs on` | IIS in-process or HTTP.sys can't enforce some limits |
| 100 | Warning | `Shared rate limiting is unavailable` | Redis circuit opened; says what each fallback does |
| 101 | Information | `Shared rate limiting restored` | Circuit closed |
| 102 | Debug | `A Redis rate limit call failed` | One call failed; carries the exception |
| 103 | Warning | `A Redis-backed rate limiter (policy` | Code uses only `AttemptAcquire`; switch to `AcquireAsync` |
| 104 | Warning | `Redis at ... is version` | Redis older than 5.0 |
| 105 | Warning | `Redis at ... has a memory limit and maxmemory-policy` | Keys can be evicted; use `noeviction` |
| 106 | Debug | `Could not check the Redis server` | `INFO` refused |
| 107 | Warning | `Redis at ... is reached without TLS` | Plain text to another host, outside Development; add `ssl=true` |
| 200 | Warning | `ApiPipeline.NET.Versioning 2.0 reads deprecation dates` | 1.x `ApiVersionDeprecationOptions` section present |
| 201 | Warning | `API version deprecation policies are configured, but` | Version reporting off |

Unused, never reassigned: 41, 50, 51, 60, 61.

## Configuration changes after startup

Settings are read once through `IOptions<T>`; edits apply on restart. With `ValidateOnStart()` the
framework's options monitor throws inside its change callback on an invalid edit; this never reaches
requests or crashes the process (`PollingFileWatcherConfigurationTests`), but may show in
first-chance exception logs. Don't read `IOptionsMonitor<ApiPipeline*Options>` yourself: it throws
after an invalid edit.

## Testing

Each host has its own meter (from `IMeterFactory`). Filter a `MeterListener` on
`instrument.Meter.Scope` to see one host's measurements.
