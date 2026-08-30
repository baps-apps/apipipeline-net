# Correlation IDs and Trace IDs

`WithCorrelationId()` returns two IDs on every response:

| Header | Meaning | Source |
| --- | --- | --- |
| `X-Correlation-Id` | One business operation, across traces, queues and jobs; forwarded downstream | The caller's, if valid; else a generated GUID |
| `X-Trace-Id` | This request's W3C trace ID, as your tracing backend shows it | The request activity; omitted without one |

Both are available as `HttpContext.GetCorrelationId()` and `GetTraceId()` (namespace
`ApiPipeline.NET.Correlation`). Request logs carry `CorrelationId` in their scope (the host adds
`TraceId`), and the request span gets the tag `apipipeline.correlation_id`. A generated correlation
ID is never the trace ID. Neither ID is an identity: never authorize or rate limit on them.

## Setup

```csharp
builder.Services.AddApiPipeline(builder.Configuration);

app.UseApiPipeline(pipeline => pipeline
    .WithForwardedHeaders()
    .WithCorrelationId());
```

No `Enabled` flag: calling `WithCorrelationId()` turns it on.

## Options (section `ApiPipeline:CorrelationId`)

| Option | Default | Notes |
| --- | --- | --- |
| `AcceptInboundCorrelationId` | `true` | `false` always generates one. Doesn't cover the trace ID: ASP.NET Core continues an inbound `traceparent`; strip it at an untrusted ingress. |
| `EmitTraceIdHeader` | `true` | `false` omits `X-Trace-Id` from responses only |
| `MaxLength` | `128` | 8–256 |

An inbound ID is accepted only if it is 1 to `MaxLength` characters of `A-Z a-z 0-9 - _ .`, sent
once. Otherwise it is replaced, counted in `apipipeline.correlation_id.rejected` and logged at Debug.

## Forwarding to downstream services

`HttpClient` forwards `traceparent` itself. For the correlation ID, add the handler per client:

```csharp
builder.Services
    .AddHttpClient("downstream", c => c.BaseAddress = new Uri("https://downstream.example.com"))
    .AddApiPipelineCorrelationIdPropagation();
```

A request that already sets `X-Correlation-Id` keeps it; calls outside an HTTP request send none.

## Errors and logs

The headers are set when the response starts, so error responses have them. For the IDs in error
bodies and in the unhandled-exception log line, use the README's
[error handling](../../README.md#error-handling): the exception handler must be inside the pipeline,
after `WithCorrelationId()`, to log within the correlation scope.

## Troubleshooting

| Symptom | Cause |
| --- | --- |
| No `X-Correlation-Id` | `WithCorrelationId()` missing, or a proxy strips it |
| Your ID comes back different | Invalid, sent twice, or `AcceptInboundCorrelationId: false` |
| No `X-Trace-Id` | `EmitTraceIdHeader: false`, or no request activity (enable tracing) |
| Trace ID finds nothing | Not sampled or not exported |
| Logs lack `CorrelationId` | The provider doesn't write scopes (console: `IncludeScopes: true`) |
| Browser reads `null` | Origin not allowed by CORS |

## Why not `Microsoft.AspNetCore.HeaderPropagation`?

It copies the inbound header, so a generated correlation ID wouldn't be forwarded, and it would add
the core package's first runtime dependency.

## References

- [Distributed tracing concepts (.NET)](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/distributed-tracing-concepts)
- [W3C Trace Context](https://www.w3.org/TR/trace-context/)
