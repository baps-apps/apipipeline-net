# Structured RFC 7807 Exception Handling

## What it does

ApiPipeline.NET registers ASP.NET Core's `ProblemDetails` services with custom enrichment, then applies `UseExceptionHandler` and `UseStatusCodePages` in the middleware pipeline. Unhandled exceptions and non-success status codes are converted into structured RFC 7807 `ProblemDetails` JSON responses with correlation ID and trace ID for debugging.

## Why it matters

- **Consistent error format**: all errors (500, 404, 429, etc.) return the same RFC 7807 JSON structure. Clients can parse errors uniformly.
- **Anti-caching**: every error response includes `Cache-Control: no-store`, preventing proxies and CDNs from caching error responses.
- **Correlation**: error responses include `correlationId` and `traceId` extensions, enabling direct log lookup from a failed response.
- **Telemetry**: exceptions caught by the handler increment the `apipipeline.exceptions.handled` metric.
- **Safe by default**: stack traces and internal exception details are never exposed in the response body.

## How it works

```text
Request → Middleware pipeline → Exception thrown
  ↓
  UseExceptionHandler catches it
  ↓
  IProblemDetailsService.WriteAsync()
  ↓
  Customization callback:
    • Sets Cache-Control: no-store
    • Adds correlationId from HttpContext.Items
    • Adds traceId from Activity.Current
    • Increments apipipeline.exceptions.handled metric
  ↓
  Response:
  {
    "type": "https://tools.ietf.org/html/rfc9110#section-15.6.1",
    "title": "An error occurred while processing your request.",
    "status": 500,
    "correlationId": "abc-123",
    "traceId": "00-abcdef..."
  }
```

### Status code pages

`UseStatusCodePages` ensures non-exception HTTP errors (e.g., 404 from no matching route, 401 from auth failure) also return `ProblemDetails` format instead of empty responses.

### Rate limiting rejection format

The rate limiter uses the same `IProblemDetailsService` for 429 responses:

```json
{
  "type": "https://tools.ietf.org/html/rfc6585#section-4",
  "title": "Too Many Requests",
  "status": 429,
  "detail": "Rate limit exceeded. Retry after the duration indicated by the Retry-After header.",
  "correlationId": "abc-123",
  "traceId": "00-abcdef..."
}
```

## Registration

```csharp
// Aggregate (recommended) — automatically registers exception handling
builder.Services.AddApiPipeline(builder.Configuration);

// Or standalone
builder.Services.AddApiPipelineExceptionHandler();
```

`AddApiPipeline()` includes exception handler registration by default. You can disable it:

```csharp
builder.Services.AddApiPipeline(builder.Configuration, options =>
{
    options.AddExceptionHandler = false;  // handle exceptions yourself
});
```

### Pipeline

```csharp
app.UseApiPipeline(pipeline => pipeline
    .WithForwardedHeaders()
    .WithCorrelationId()      // must be before exception handler
    .WithExceptionHandler()   // catches all downstream exceptions
    // ...
);
```

The exception handler must run after the correlation ID middleware (so errors include the correlation ID) and before all business logic middleware.

## Options

This feature has no dedicated configuration options. It is registered via `AddApiPipelineExceptionHandler()` or included in `AddApiPipeline()`.

The ProblemDetails customization is applied through `AddProblemDetails(options => ...)` and includes:
- `Cache-Control: no-store` on all error responses.
- `correlationId` extension from `HttpContext.Items["X-Correlation-Id"]`.
- `traceId` extension from `Activity.Current?.Id` or `HttpContext.TraceIdentifier`.
- `apipipeline.exceptions.handled` counter increment (only for actual exceptions).

## Response examples

### 500 Internal Server Error (unhandled exception)

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.6.1",
  "title": "An error occurred while processing your request.",
  "status": 500,
  "correlationId": "0af7651916cd43dd8448eb211c80319c",
  "traceId": "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01"
}
```

Headers: `Cache-Control: no-store`, `Content-Type: application/problem+json`.

### 404 Not Found (no matching route)

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.5",
  "title": "Not Found",
  "status": 404,
  "correlationId": "abc-123",
  "traceId": "00-..."
}
```

### 401 Unauthorized

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.2",
  "title": "Unauthorized",
  "status": 401,
  "correlationId": "abc-123",
  "traceId": "00-..."
}
```

## Production recommendations

| Recommendation | Why |
|---|---|
| Always enable exception handling | Unhandled exceptions should never leak stack traces |
| Position after correlation ID in the pipeline | Error responses include the correlation ID for debugging |
| Monitor `apipipeline.exceptions.handled` metric | Rising exception count indicates application bugs |
| Use correlation ID from error responses to find logs | `correlationId` links the client response to server-side logs |
| Don't add custom exception-to-status-code mapping in the handler | Use middleware or `IExceptionHandler` for specific mappings; the pipeline handler is the fallback |

## Non-production recommendations

| Recommendation | Why |
|---|---|
| Keep exception handling enabled | Consistent error format helps debug during development |
| In Development, ASP.NET Core shows the developer exception page by default | This middleware takes over — verify you're seeing ProblemDetails, not HTML error pages |
| Test that error responses include `correlationId` | Validates the middleware ordering is correct |

## Troubleshooting

### Error responses return HTML instead of JSON

1. Verify `WithExceptionHandler()` is called in `UseApiPipeline`.
2. Verify `AddApiPipelineExceptionHandler()` is registered (or `AddApiPipeline()` is used).
3. Check that the request includes `Accept: application/json` header.

### Error responses missing `correlationId`

The correlation ID middleware must run before the exception handler:

```csharp
app.UseApiPipeline(pipeline => pipeline
    .WithCorrelationId()      // ← must be before
    .WithExceptionHandler()   // ← this
    // ...
);
```

`UseApiPipeline` enforces this order automatically.

### `Cache-Control: no-store` missing from error responses

Verify `AddApiPipelineExceptionHandler()` is registered. The `Cache-Control` header is set in the `CustomizeProblemDetails` callback, not in the middleware pipeline.

### Exception details leaked in response

ApiPipeline.NET does not include exception messages or stack traces in ProblemDetails responses. If you see them, check for custom `IExceptionHandler` registrations or middleware that writes exception details before the pipeline handler catches them.

### Metric: `apipipeline.exceptions.handled`

This counter increments only for actual exceptions (not 404s or 401s from status code pages). Use it to distinguish between expected errors and application bugs.

## References

- [Handle errors in ASP.NET Core APIs](https://learn.microsoft.com/en-us/aspnet/core/web-api/handle-errors) — `ProblemDetails`, `UseExceptionHandler`, and `UseStatusCodePages`
- [RFC 9457: Problem Details for HTTP APIs](https://www.rfc-editor.org/rfc/rfc9457) — the current standard (obsoletes RFC 7807) defining the `application/problem+json` format
- [ProblemDetails class reference](https://learn.microsoft.com/en-us/dotnet/api/microsoft.aspnetcore.mvc.problemdetails) — API reference for `ProblemDetails` extensions and properties

## Related

- [correlation-id.md](correlation-id.md) — correlation IDs in error responses
- [rate-limiting.md](rate-limiting.md) — 429 responses use the same ProblemDetails service
- [RUNBOOK.md](../RUNBOOK.md) — investigating 5xx errors
