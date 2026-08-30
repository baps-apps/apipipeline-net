# CORS

Lets browser apps on listed origins call the API. Responses always expose `X-Correlation-Id`,
`X-Trace-Id`, `Retry-After`, `RateLimit` and `RateLimit-Policy` to browser JavaScript. Rejected
origins are counted in `apipipeline.cors.rejected`.

## Setup

```json
{
  "ApiPipeline": {
    "Cors": {
      "Enabled": true,
      "AllowedOrigins": [ "https://app.example.com" ],
      "AllowCredentials": true
    }
  }
}
```

```csharp
builder.Services.AddApiPipeline(builder.Configuration);

app.UseApiPipeline(pipeline => pipeline
    .WithCors()               // before auth and rate limiting, so preflights are answered
    .WithAuthentication()
    .WithAuthorization());
```

For Development, `"Cors": { "Enabled": true, "AllowAllInDevelopment": true }` allows everything.

## Options (section `ApiPipeline:Cors`)

| Property | Default | Notes |
| --- | --- | --- |
| `Enabled` | `false` | |
| `AllowAllInDevelopment` | `false` | Ignored outside Development |
| `AllowedOrigins` | none | Exact [origins](#origin-format), or `["*"]` alone. None rejects every cross-origin request. |
| `AllowedMethods` | `GET, POST, PUT, PATCH, DELETE, OPTIONS` | A list replaces the default; `["*"]` for any; `[]` fails startup |
| `AllowedHeaders` | `Content-Type, Authorization, X-Correlation-Id` | Same; list every header the browser sends |
| `AllowCredentials` | `false` | Requires explicit origins, `https://` outside Development |
| `PreflightMaxAge` | none | `Access-Control-Max-Age`, whole seconds up to a day; unset, browsers re-check after 5 s |

With response caching on, `Vary: Origin` is added.

## Your own `AddCors` policies

The package wraps the policy provider in effect and answers only the default lookup.

| Your app has | Result |
| --- | --- |
| Named policies with `RequireCors("name")` | Work unchanged |
| An `ICorsPolicyProvider` registered before `AddApiPipeline` | Kept; answers named lookups, and all lookups while CORS is off. With CORS on, a warning names it. |
| An `ICorsPolicyProvider` registered after, CORS on | Startup fails outside Development: register it before |
| `AddDefaultPolicy(...)`, CORS on | Startup fails outside Development (warning in Development): move it to `ApiPipeline:Cors`, name it, or turn CORS off |
| `AddDefaultPolicy(...)`, CORS off | Your policy applies |

## Origin format

Origins must be exactly what browsers send, `scheme://host[:port]`, or they never match. Startup
fails for anything else, naming the value to use:

| Entry | Use |
| --- | --- |
| `https://app.example.com/` or `/path` | `https://app.example.com` |
| `https://app.example.com:443` (default port) | `https://app.example.com` |
| `https://*.example.com` | List each origin |
| `null` | Remove: any sandboxed page or local file could call the API |
| `*` with other origins | `["*"]` alone |
| `http://...` with credentials, outside Development | Serve it over HTTPS |

Non-default ports, IPv6, internationalized hosts and app schemes (`capacitor://localhost`) are fine.

## Troubleshooting

```bash
curl -sD - -o /dev/null -X OPTIONS https://api.example.com/api/resource \
  -H "Origin: https://app.example.com" -H "Access-Control-Request-Method: POST" | grep -i access-control
```

| Symptom | Check |
| --- | --- |
| No `Access-Control-Allow-Origin` | Exact origin listed (scheme, port), `Enabled` and `WithCors()` |
| Works only in Development | `AllowAllInDevelopment` is ignored elsewhere |
| "Request header field X is not allowed" | Add it to `AllowedHeaders` |
| Preflight gets 401/403/429 | Middleware added before `UseApiPipeline` |
| Startup error | The message says which entry and what to use |

## References

- [Enable CORS in ASP.NET Core](https://learn.microsoft.com/en-us/aspnet/core/security/cors)
