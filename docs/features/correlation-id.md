# Correlation IDs

## What it does

ApiPipeline.NET's `CorrelationIdMiddleware` ensures every request and response carries a unique `X-Correlation-Id` header. Incoming IDs are validated against a strict pattern to prevent header injection attacks. The ID is propagated to `HttpContext.Items`, response headers, the current `Activity` (for distributed tracing), and the `ILogger` scope (for structured logging).

## Why it matters

- **Cross-service tracing**: a single ID follows a request across microservices, load balancers, and log aggregators.
- **Incident debugging**: correlate error responses to specific log entries using the ID from the `X-Correlation-Id` response header.
- **Security**: incoming IDs are validated with a source-generated regex — invalid values (containing newlines, control characters, or exceeding 128 chars) are rejected and replaced, preventing header injection attacks.
- **Zero-configuration**: no options to configure. If you call `WithCorrelationId()`, it just works.

## How it works

```text
Request arrives
  ↓
Has X-Correlation-Id header?
  ├── Yes → Validate with regex: ^[a-zA-Z0-9\-_.]{1,128}$
  │          ├── Valid → Use as-is
  │          └── Invalid → Generate new ID (Activity.TraceId or GUID)
  └── No → Generate new ID (Activity.TraceId or GUID)
  ↓
Store in HttpContext.Items["X-Correlation-Id"]
Set on Activity.Current tag: "correlation_id"
Echo on response header: X-Correlation-Id
Begin ILogger scope with CorrelationId
  ↓
Execute downstream middleware
```

### Validation pattern

The source-generated regex `^[a-zA-Z0-9\-_.]{1,128}$` allows:

| Allowed | Examples |
|---|---|
| Alphanumeric | `abc123`, `ABC`, `42` |
| Hyphens | `req-abc-123` |
| Underscores | `req_abc_123` |
| Dots | `trace.id.123` |
| 1–128 characters | Up to 128 chars total |

**Rejected**: newlines (`\n`), carriage returns (`\r`), spaces, colons, semicolons, any non-ASCII characters, or values exceeding 128 characters.

### ID generation fallback

When generating a new ID, the middleware prefers `Activity.Current?.TraceId` (the W3C trace ID from distributed tracing). This links the correlation ID to the OpenTelemetry trace. If no `Activity` is active, a `Guid.NewGuid().ToString("N")` is used.

## Registration

```csharp
// Aggregate (recommended) — automatically registers CorrelationIdMiddleware
builder.Services.AddApiPipeline(builder.Configuration);

// Or standalone
builder.Services.AddCorrelationId();
```

### Pipeline

```csharp
app.UseApiPipeline(pipeline => pipeline
    .WithForwardedHeaders()
    .WithCorrelationId()    // early in pipeline — before exception handler
    .WithExceptionHandler()
    // ...
);
```

The correlation ID middleware runs before the exception handler so that error responses include the correlation ID.

## Options

This feature has no configuration options. It is always active when registered.

## Reading the correlation ID in your code

### From HttpContext.Items

```csharp
app.MapGet("/api/resource", (HttpContext context) =>
{
    var correlationId = context.Items["X-Correlation-Id"]?.ToString();
    // Use in downstream calls, logging, etc.
    return Results.Ok(new { CorrelationId = correlationId });
});
```

### From response headers (client-side)

```bash
curl -s -D - https://api.example.com/health | grep -i x-correlation-id
# X-Correlation-Id: 0af7651916cd43dd8448eb211c80319c
```

### Forwarding to downstream services

```csharp
app.MapGet("/api/resource", async (HttpContext context, HttpClient client) =>
{
    var correlationId = context.Items["X-Correlation-Id"]?.ToString();

    var request = new HttpRequestMessage(HttpMethod.Get, "https://downstream-service/api/data");
    request.Headers.Add("X-Correlation-Id", correlationId);

    var response = await client.SendAsync(request);
    // ...
});
```

### Serilog enrichment

```csharp
// The middleware already pushes CorrelationId into ILogger.BeginScope.
// With Serilog, this appears automatically in structured logs:
// { "CorrelationId": "abc-123", "Message": "Processing request..." }
```

## Configuration examples

No configuration is needed. The middleware is activated by calling `WithCorrelationId()` in the pipeline.

### Sending a known correlation ID

```bash
# Client sends a valid ID — server echoes it
curl -H "X-Correlation-Id: my-request-123" https://api.example.com/api/resource -v
# Response includes: X-Correlation-Id: my-request-123

# Client sends an invalid ID — server generates a new one
curl -H "X-Correlation-Id: invalid<>id" https://api.example.com/api/resource -v
# Response includes: X-Correlation-Id: (server-generated value)
```

## Production recommendations

| Recommendation | Why |
|---|---|
| Always include `WithCorrelationId()` in the pipeline | Essential for cross-service tracing and incident debugging |
| Position before the exception handler | Error responses include the correlation ID |
| Configure upstream proxies to forward `X-Correlation-Id` | Preserves the ID through load balancers |
| Use the same header in downstream service calls | Creates an end-to-end trace across microservices |
| Pair with OpenTelemetry | The correlation ID is set as an `Activity` tag, linking to traces |

## Non-production recommendations

| Recommendation | Why |
|---|---|
| Keep correlation ID enabled | Simplifies debugging in dev/test environments |
| Use known IDs in integration tests | Makes test assertions and log searching easier |

```csharp
// In tests, send a known correlation ID
var response = await client.SendAsync(new HttpRequestMessage
{
    RequestUri = new Uri("/api/resource", UriKind.Relative),
    Headers = { { "X-Correlation-Id", "test-correlation-123" } }
});

Assert.Equal("test-correlation-123", response.Headers.GetValues("X-Correlation-Id").Single());
```

## Troubleshooting

### Correlation ID missing from responses

1. Verify `WithCorrelationId()` is called in `UseApiPipeline`.
2. Check that upstream proxies are not stripping the `X-Correlation-Id` header.

```bash
curl -s -D - https://api.example.com/health | grep -i x-correlation-id
```

### Correlation ID changes even when sent by client

The client-provided ID was rejected by validation. Check:
- Length must be 1–128 characters.
- Only alphanumeric, hyphens, underscores, and dots are allowed.
- No spaces, newlines, or special characters.

### Correlation ID not appearing in logs

The middleware uses `ILogger.BeginScope` with `CorrelationId`. Check that your logging provider supports scoped properties:
- **Serilog**: Scoped properties appear automatically in structured output.
- **Console logger**: Add `IncludeScopes = true` in configuration.
- **Application Insights**: Scoped properties are captured by default.

### Telemetry metric

The `apipipeline.correlation_id.processed` counter increments for every processed request. Use this to verify the middleware is active.

## References

- [Distributed tracing concepts — .NET](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/distributed-tracing-concepts) — `System.Diagnostics.Activity`, `TraceId`, and span propagation
- [W3C Trace Context specification](https://www.w3.org/TR/trace-context/) — the trace ID format used by `Activity.Current.TraceId`
- [Collect a distributed trace — .NET](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/distributed-tracing-collection-walkthroughs) — collecting traces with OpenTelemetry in .NET
- [ASP.NET Core logging with scopes](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/logging/#log-scopes) — `ILogger.BeginScope` used for `CorrelationId`

## Related

- [exception-handling.md](exception-handling.md) — error responses include correlation ID
- [RUNBOOK.md](../RUNBOOK.md) — using correlation IDs for incident investigation
- [OPERATIONS.md](../OPERATIONS.md) — observability guidance
