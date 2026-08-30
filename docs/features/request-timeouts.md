# Request Timeouts

A default timeout for every endpoint, through ASP.NET Core's request timeouts middleware, so a hung
dependency can't hold requests open indefinitely.

## Setup

```json
{
  "ApiPipeline": {
    "RequestTimeouts": { "Enabled": true, "DefaultTimeout": "00:00:30" }
  }
}
```

```csharp
app.UseApiPipeline(pipeline => pipeline
    .WithCorrelationId()
    .WithSecurityHeaders()
    .WithRequestTimeouts()      // after the header phases, before authentication
    .WithAuthentication());
```

| Property | Default | Notes |
| --- | --- | --- |
| `Enabled` | `false` | |
| `DefaultTimeout` | `00:00:30` | 1 s–1 h; keep it below the ingress timeout |
| `TimeoutStatusCode` | `504` | 400–599 |

Per endpoint, use the framework's own:

```csharp
app.MapGet("/reports/annual", GenerateAsync).WithRequestTimeout(TimeSpan.FromMinutes(2));
app.MapGet("/events", StreamAsync).DisableRequestTimeout();   // streams, long polling
```

## How a timeout works

The framework only cancels `HttpContext.RequestAborted`:

- Code must pass that token on (`HttpClient`, EF Core); code that ignores it runs to completion.
- A response that has started can't change status; a stream is cut off.
- Timeouts never fire with a debugger attached.

The default policy's timeout response has an RFC 9457 body through your `IProblemDetailsService`.
An endpoint's own `WithRequestTimeout` policy gets the bare status unless it sets
`WriteTimeoutResponse`. Slow clients are bounded separately by [request limits](request-limits.md).

## Troubleshooting

| Symptom | Cause |
| --- | --- |
| Nothing times out | Disabled, phase missing, debugger attached, or the token is ignored |
| Stream cut off | Add `DisableRequestTimeout()` |
| Caller sees the ingress error | Ingress timeout shorter than `DefaultTimeout` |

## References

- [Request timeouts middleware in ASP.NET Core](https://learn.microsoft.com/en-us/aspnet/core/performance/timeouts)
