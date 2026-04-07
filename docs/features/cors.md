# CORS (Cross-Origin Resource Sharing)

## What it does

ApiPipeline.NET provides configuration-driven CORS with two operational modes: a permissive development mode that allows all origins, and a restrictive production mode that enforces explicit origin, method, and header allowlists. A `LiveConfigCorsPolicyProvider` resolves the correct policy based on the current environment and configuration.

## Why it matters

- **Browser security**: browsers enforce the Same-Origin Policy. Without CORS headers, frontend apps on different origins cannot call your API.
- **Development friction eliminated**: `AllowAllInDevelopment: true` removes CORS as a blocker during local development without compromising production security.
- **Safe defaults**: `AllowedHeaders` defaults to `["Content-Type", "Authorization", "X-Correlation-Id"]` instead of wildcard, following OWASP recommendations.
- **Credentials guard**: the CORS spec forbids wildcard origins with credentials. ApiPipeline.NET validates this at startup and fails fast.

## How it works

```text
Development + AllowAllInDevelopment: true
  → Uses "AllowAll" policy (any origin, any method, any header)

Production (or AllowAllInDevelopment: false)
  → Uses "Configured" policy built from AllowedOrigins/Methods/Headers
  → AllowCredentials sets Access-Control-Allow-Credentials: true
```

The `LiveConfigCorsPolicyProvider` is registered as `ICorsPolicyProvider` and resolves the correct policy at runtime. It checks `IHostEnvironment.IsDevelopment()` and the current `CorsSettings` via `IOptionsMonitor`.

### Preflight flow

```text
Browser → OPTIONS /api/resource
          Origin: https://app.example.com
          Access-Control-Request-Method: POST
          Access-Control-Request-Headers: Content-Type, Authorization

Server → 204 No Content
          Access-Control-Allow-Origin: https://app.example.com
          Access-Control-Allow-Methods: GET, POST, PUT, PATCH, DELETE, OPTIONS
          Access-Control-Allow-Headers: Content-Type, Authorization, X-Correlation-Id
          Access-Control-Allow-Credentials: true
          Access-Control-Max-Age: 7200
```

## Registration

```csharp
// Aggregate (recommended)
builder.Services.AddApiPipeline(builder.Configuration);

// Or standalone
builder.Services.AddCors(builder.Configuration);
```

### Pipeline

```csharp
app.UseApiPipeline(pipeline => pipeline
    // ...
    .WithCors()              // before auth so preflight isn't rate-limited
    .WithAuthentication()
    .WithAuthorization()
    .WithRateLimiting()
    // ...
);
```

CORS is positioned before authentication in the pipeline so that preflight `OPTIONS` requests are not blocked by auth or consumed by rate limiting.

## All available options

### `CorsSettings` (JSON section: `CorsOptions`)

| Property | Type | Default | Description |
|---|---|---|---|
| `Enabled` | `bool` | `false` | Master switch. When `false`, no CORS middleware is applied. |
| `AllowAllInDevelopment` | `bool` | `false` | When `true` **and** in Development environment, allows all origins, methods, and headers. |
| `AllowedOrigins` | `string[]?` | `null` | Explicit list of allowed origins. Must include scheme and port (e.g., `https://app.example.com`). |
| `AllowedMethods` | `string[]?` | `["GET", "POST", "PUT", "PATCH", "DELETE", "OPTIONS"]` | HTTP methods allowed for cross-origin requests. Use `["*"]` for unrestricted. |
| `AllowedHeaders` | `string[]?` | `["Content-Type", "Authorization", "X-Correlation-Id"]` | Request headers allowed from cross-origin requests. Use `["*"]` for unrestricted. |
| `AllowCredentials` | `bool` | `false` | Whether to include `Access-Control-Allow-Credentials: true`. When `true`, `AllowedOrigins` must be explicit (no wildcard). |

### Well-known policy names

| Constant | Name | When used |
|---|---|---|
| `CorsPolicyNames.AllowAll` | `"AllowAll"` | Development with `AllowAllInDevelopment: true` |
| `CorsPolicyNames.Configured` | `"Configured"` | Production or when `AllowAllInDevelopment: false` |

## Configuration examples

### Development (allow all)

```json
{
  "CorsOptions": {
    "Enabled": true,
    "AllowAllInDevelopment": true,
    "AllowedOrigins": [],
    "AllowedMethods": ["GET", "POST", "PUT", "PATCH", "DELETE", "OPTIONS"],
    "AllowedHeaders": ["Content-Type", "Authorization", "X-Correlation-Id"],
    "AllowCredentials": false
  }
}
```

In Development, all origins are allowed. In any other environment, this config would reject all cross-origin requests (empty `AllowedOrigins`).

### Production (explicit origins with credentials)

```json
{
  "CorsOptions": {
    "Enabled": true,
    "AllowAllInDevelopment": false,
    "AllowedOrigins": [
      "https://app.example.com",
      "https://admin.example.com"
    ],
    "AllowedMethods": ["GET", "POST", "PUT", "PATCH", "DELETE", "OPTIONS"],
    "AllowedHeaders": ["Content-Type", "Authorization", "X-Correlation-Id"],
    "AllowCredentials": true
  }
}
```

### Production (no credentials, wildcard headers)

```json
{
  "CorsOptions": {
    "Enabled": true,
    "AllowAllInDevelopment": false,
    "AllowedOrigins": ["https://app.example.com"],
    "AllowedMethods": ["GET", "POST"],
    "AllowedHeaders": ["*"],
    "AllowCredentials": false
  }
}
```

### Staging with dev origins

Use environment-specific config files (`appsettings.Staging.json`):

```json
{
  "CorsOptions": {
    "AllowedOrigins": [
      "https://staging-app.example.com",
      "http://localhost:3000"
    ]
  }
}
```

## Production recommendations

| Recommendation | Why |
|---|---|
| Set `AllowAllInDevelopment: false` in production config | Double-check that the allow-all policy is not active |
| List all frontend origins explicitly | Wildcard is not allowed with credentials; explicit is more secure |
| Prefer explicit `AllowedHeaders` over `["*"]` | Limits which headers can be sent cross-origin (OWASP recommendation) |
| Include `OPTIONS` in `AllowedMethods` | Required for preflight requests |
| `Vary: Origin` is added automatically when CORS + response caching are both enabled | Prevents cross-origin cache pollution (handled by ApiPipeline.NET) |

## Non-production recommendations

| Recommendation | Why |
|---|---|
| Use `AllowAllInDevelopment: true` | Eliminates CORS friction during local development |
| Keep `AllowCredentials: false` in dev unless testing auth flows | Simplifies debugging |
| Test with production-like CORS config in staging | Catches missing origins before production |

## Startup validation

CORS validates at startup. These errors crash the app intentionally:

| Error | Cause | Fix |
|---|---|---|
| `When AllowCredentials is true, AllowedOrigins must be configured` | `AllowCredentials: true` with empty/missing `AllowedOrigins` | Add explicit origins |
| `AllowedMethods must include at least one non-empty value` | Empty `AllowedMethods` with CORS enabled and `AllowAllInDevelopment: false` | Add methods or use `["*"]` |
| `AllowedHeaders must include at least one non-empty value` | Empty `AllowedHeaders` with CORS enabled and `AllowAllInDevelopment: false` | Add headers or use `["*"]` |

The methods/headers validations are skipped when `AllowAllInDevelopment: true`, since the "AllowAll" policy overrides them. The credentials validation always applies regardless of environment.

## Troubleshooting

### Browser shows CORS error

1. Check the browser console for the exact error message.
2. Verify the request `Origin` header matches one of `AllowedOrigins` exactly (including scheme, host, and port).
3. Test with a manual preflight:

```bash
curl -X OPTIONS https://api.example.com/api/resource \
  -H "Origin: https://app.example.com" \
  -H "Access-Control-Request-Method: POST" \
  -H "Access-Control-Request-Headers: Content-Type, Authorization" \
  -v 2>&1 | grep -i "access-control"
```

### CORS works in development but not staging/production

1. Verify `AllowAllInDevelopment` is `true` only in Development — it has no effect in other environments.
2. Check that `AllowedOrigins` includes the correct staging/production frontend URLs.
3. Check environment-specific config files (`appsettings.Production.json`).

### Preflight returns 401/403

CORS middleware must run **before** authentication. `UseApiPipeline` enforces this automatically. If you're wiring manually, ensure `UseCors()` is called before `UseAuthentication()`.

### Missing `Access-Control-Allow-Credentials`

Set `AllowCredentials: true` in config and ensure `AllowedOrigins` contains explicit origins (not empty).

## References

- [Enable Cross-Origin Requests (CORS) in ASP.NET Core](https://learn.microsoft.com/en-us/aspnet/core/security/cors) — Microsoft's CORS middleware documentation
- [MDN: Cross-Origin Resource Sharing (CORS)](https://developer.mozilla.org/en-US/docs/Web/HTTP/CORS) — browser-side CORS specification and behavior
- [OWASP: CORS misconfiguration](https://cheatsheetseries.owasp.org/cheatsheets/HTTP_Headers_Cheat_Sheet.html#access-control-allow-origin) — security guidance for CORS headers

## Related

- [RUNBOOK.md](../RUNBOOK.md) — incident response for CORS failures
- [TROUBLESHOOTING.md](../TROUBLESHOOTING.md) — quick diagnostics
- [ARCHITECTURE.md](../ARCHITECTURE.md) — middleware ordering (CORS before auth)
